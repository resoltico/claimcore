"""Fixed published Database rechecks; no owner-selected executable or trust root."""

import json
import os
import selectors
import subprocess
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, private_path, require
from deployment_fenced_objects import final_objects_digest
from deployment_publication import (
    match_qualification,
    package_binary,
    package_publication_files,
    verify_publication,
)
from deployment_publication_root import reviewed_publication_root

# A separately reviewed publication root must be compiled into this release package.
# No owner configuration field or environment variable may choose its bytes.
REVIEWED_PUBLICATION_ROOT_PEM = reviewed_publication_root()


RECHECK_LIMIT = 4096
RECHECK_TIMEOUT_SECONDS = 30
READ_BLOCK = 4096
MIN_WAIT_SECONDS = 0.1
FORBIDDEN_CONFIG = ("productVerifierPath", "productRecheckCommand", "publicationRootPath")
CONFIG_PATHS = (
    "fullReportPath",
    "fullReportSignaturePath",
    "restoreEvidenceIndexPath",
    "fenceReportPath",
    "fenceReportSignaturePath",
    "fencedTailPath",
    "fencedTailSignaturePath",
)
RECHECK_FIELDS = {
    "format",
    "nonce",
    "status",
    "reportSha256",
    "evidenceIndexSha256",
    "witnessCutoff",
    "witnessCutoffHash",
    "realDataReady",
}
FENCED_FIELDS = {
    "format",
    "nonce",
    "status",
    "reportSha256",
    "fenceReportSha256",
    "supplementSha256",
    "finalWalObjectSha256",
    "w1Sequence",
    "w1Hash",
    "writerGeneration",
    "realDataReady",
}


@dataclass(frozen=True)
class ProductOverrides:
    """Synthetic substitutes for the reviewed root, publication files, verifier and runner."""

    root_key: bytes | str | Path | None = None
    publication_files: tuple[Path, Path] | None = None
    product_runner: Callable[[Sequence[str]], bytes] | None = None
    binary_path: Path | None = None

    @property
    def synthetic(self) -> bool:
        """Whether any reviewed, package-fixed input was replaced."""
        return (
            (self.root_key is not None and self.root_key is not REVIEWED_PUBLICATION_ROOT_PEM)
            or self.product_runner is not None
            or self.binary_path is not None
        )


def _read_block(child: subprocess.Popen[bytes], deadline: float, maximum: int) -> bytes:
    stream = child.stdout
    if stream is None:
        msg = "deployment-probe-unavailable"
        raise DeploymentRefusalError(msg)
    result = bytearray()
    with selectors.DefaultSelector() as selector:
        selector.register(stream, selectors.EVENT_READ)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            require(remaining > 0, "deployment-probe-timeout")
            for key, _ in selector.select(remaining):
                block = os.read(key.fd, min(READ_BLOCK, maximum + 1 - len(result)))
                if not block:
                    selector.unregister(key.fileobj)
                else:
                    result.extend(block)
                    require(len(result) <= maximum, "deployment-response-limit")
    return bytes(result)


def bounded(command: Sequence[str], maximum: int, timeout: int) -> bytes:
    """Run `command`, returning at most `maximum` bytes of stdout within `timeout` seconds."""
    try:
        child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    except OSError:
        msg = "deployment-probe-unavailable"
        raise DeploymentRefusalError(msg) from None
    deadline = time.monotonic() + timeout
    try:
        result = _read_block(child, deadline, maximum)
        try:
            status = child.wait(timeout=max(MIN_WAIT_SECONDS, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            msg = "deployment-probe-timeout"
            raise DeploymentRefusalError(msg) from None
        require(status == 0, "deployment-probe-unavailable")
        return result
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()


def _published(config: JsonObject, overrides: ProductOverrides) -> tuple[Path, JsonObject, str]:
    """Verify the package-fixed publication and return its binary, manifest and digest."""
    require(
        not any(name in config for name in FORBIDDEN_CONFIG), "config-selected-product-verifier"
    )
    root_key = REVIEWED_PUBLICATION_ROOT_PEM if overrides.root_key is None else overrides.root_key
    require(root_key is not None, "production-evidence-unavailable")
    binary = package_binary() if overrides.binary_path is None else overrides.binary_path
    manifest, signature = overrides.publication_files or package_publication_files()
    publication, publication_sha = verify_publication(root_key, manifest, signature, binary)
    return binary, publication, publication_sha


def _recheck(
    command: list[str], overrides: ProductOverrides, fields: set[str], shape_category: str
) -> JsonObject:
    runner = overrides.product_runner
    response = (
        bounded(command, RECHECK_LIMIT, RECHECK_TIMEOUT_SECONDS)
        if runner is None
        else runner(command)
    )
    require(
        isinstance(response, bytes) and len(response) <= RECHECK_LIMIT, "product-recheck-response"
    )
    result: JsonObject = json.loads(response)
    require(isinstance(result, dict) and set(result) == fields, shape_category)
    return result


def product_recheck(
    config: JsonObject,
    nonce: str,
    qualification_sha: str,
    qualification: JsonObject,
    *,
    overrides: ProductOverrides | None = None,
) -> JsonObject:
    """Recheck the restore report with the packaged verifier."""
    overrides = overrides or ProductOverrides()
    binary, publication, publication_sha = _published(config, overrides)
    match_qualification(publication, qualification, qualification_sha, config)
    command = [
        "dotnet",
        str(binary),
        "verify-restore-report",
        str(private_path(config["fullReportPath"])),
        str(private_path(config["fullReportSignaturePath"])),
        str(private_path(config["restoreEvidenceIndexPath"])),
        nonce,
    ]
    result = _recheck(command, overrides, RECHECK_FIELDS, "product-recheck-shape")
    require(
        result["format"] == "claimcore-restore-recheck-1"
        and result["nonce"] == nonce
        and result["status"] == "EVIDENCE_RECHECKED"
        and result["reportSha256"] == qualification_sha
        and result["evidenceIndexSha256"] == qualification["evidenceIndexSha256"]
        and result["witnessCutoff"] == qualification["witnessCutoff"]
        and result["witnessCutoffHash"] == qualification["witnessCutoffHash"]
        and result["realDataReady"] is False,
        "product-recheck-mismatch",
    )
    return {"publicationSha256": publication_sha, "syntheticOnly": overrides.synthetic}


def _check_publication_link(
    publication: JsonObject, report: JsonObject, tail: JsonObject, config: JsonObject
) -> None:
    require(
        publication["installationId"] == report["installationId"]
        and publication["lineageId"] == report["lineageId"]
        and publication["epoch"] == report["epoch"]
        and publication["writerGeneration"] == tail["newGeneration"]
        and publication["witnessCutoff"] == tail["w1Sequence"]
        and publication["witnessCutoffHash"] == tail["w1Hash"]
        and publication["reportSignerKeyId"] == report["reportSignerKeyId"]
        and publication["checkpointSignerKeyId"] == tail["checkpointSignerKeyId"]
        and publication["verifierBinarySha256"] == config["productVerifierSha256"],
        "publication-fenced-link",
    )


def product_fenced_recheck(
    config: JsonObject,
    nonce: str,
    report_sha: str,
    report: JsonObject,
    fenced: JsonObject,
    *,
    overrides: ProductOverrides | None = None,
) -> JsonObject:
    """Recheck the fenced recovery tail with the packaged verifier."""
    overrides = overrides or ProductOverrides()
    binary, publication, publication_sha = _published(config, overrides)
    tail = fenced["tail"]
    _check_publication_link(publication, report, tail, config)
    paths = [private_path(config[name]) for name in CONFIG_PATHS]
    command = ["dotnet", str(binary), "verify-fenced-tail", *(str(path) for path in paths), nonce]
    result = _recheck(command, overrides, FENCED_FIELDS, "product-fenced-shape")
    require(
        result["format"] == "claimcore-fenced-tail-recheck-1"
        and result["nonce"] == nonce
        and result["status"] == "EVIDENCE_RECHECKED"
        and result["reportSha256"] == report_sha
        and result["fenceReportSha256"] == fenced["fenceSha256"]
        and result["supplementSha256"] == fenced["tailSha256"]
        and result["finalWalObjectSha256"] == final_objects_digest(tail)
        and result["w1Sequence"] == tail["w1Sequence"]
        and result["w1Hash"] == tail["w1Hash"]
        and result["writerGeneration"] == tail["newGeneration"]
        and result["realDataReady"] is False,
        "product-fenced-mismatch",
    )
    return {"publicationSha256": publication_sha, "syntheticOnly": overrides.synthetic}
