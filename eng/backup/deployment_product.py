"""Fixed published Database rechecks; no owner-selected executable or trust root."""

import json
import os
import selectors
import subprocess
import time

from deployment_common import DeploymentRefusal, private_path, require
from deployment_fenced import final_objects_digest
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


def bounded(command, maximum, timeout):
    try:
        child = subprocess.Popen(
            command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
        )
    except OSError:
        raise DeploymentRefusal("deployment-probe-unavailable") from None
    result = bytearray()
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(child.stdout, selectors.EVENT_READ)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                require(remaining > 0, "deployment-probe-timeout")
                for key, _ in selector.select(remaining):
                    block = os.read(key.fd, min(4096, maximum + 1 - len(result)))
                    if not block:
                        selector.unregister(key.fileobj)
                    else:
                        result.extend(block)
                        require(len(result) <= maximum, "deployment-response-limit")
        try:
            status = child.wait(timeout=max(0.1, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise DeploymentRefusal("deployment-probe-timeout") from None
        require(status == 0, "deployment-probe-unavailable")
        return bytes(result)
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()


def product_recheck(
    config,
    nonce,
    qualification_sha,
    qualification,
    *,
    root_key=None,
    publication_files=None,
    product_runner=None,
    binary_path=None,
):
    require(
        not any(
            name in config
            for name in (
                "productVerifierPath",
                "productRecheckCommand",
                "publicationRootPath",
            )
        ),
        "config-selected-product-verifier",
    )
    root_key = REVIEWED_PUBLICATION_ROOT_PEM if root_key is None else root_key
    require(root_key is not None, "production-evidence-unavailable")
    binary = package_binary() if binary_path is None else binary_path
    manifest, signature = publication_files or package_publication_files()
    publication, publication_sha = verify_publication(
        root_key, manifest, signature, binary
    )
    match_qualification(publication, qualification, qualification_sha, config)
    report = private_path(config["fullReportPath"])
    report_signature = private_path(config["fullReportSignaturePath"])
    index = private_path(config["restoreEvidenceIndexPath"])
    command = [
        "dotnet",
        str(binary),
        "verify-restore-report",
        str(report),
        str(report_signature),
        str(index),
        nonce,
    ]
    response = (
        bounded(command, 4096, 30)
        if product_runner is None
        else product_runner(command)
    )
    require(
        isinstance(response, bytes) and len(response) <= 4096,
        "product-recheck-response",
    )
    result = json.loads(response)
    require(
        isinstance(result, dict)
        and set(result)
        == {
            "format",
            "nonce",
            "status",
            "reportSha256",
            "evidenceIndexSha256",
            "witnessCutoff",
            "witnessCutoffHash",
            "realDataReady",
        },
        "product-recheck-shape",
    )
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
    return {
        "publicationSha256": publication_sha,
        "syntheticOnly": root_key is not REVIEWED_PUBLICATION_ROOT_PEM
        or product_runner is not None
        or binary_path is not None,
    }


def product_fenced_recheck(
    config,
    nonce,
    report_sha,
    report,
    fenced,
    *,
    root_key=None,
    publication_files=None,
    product_runner=None,
    binary_path=None,
):
    require(
        not any(
            name in config
            for name in (
                "productVerifierPath",
                "productRecheckCommand",
                "publicationRootPath",
            )
        ),
        "config-selected-product-verifier",
    )
    root_key = REVIEWED_PUBLICATION_ROOT_PEM if root_key is None else root_key
    require(root_key is not None, "production-evidence-unavailable")
    binary = package_binary() if binary_path is None else binary_path
    manifest, signature = publication_files or package_publication_files()
    publication, publication_sha = verify_publication(
        root_key, manifest, signature, binary
    )
    tail = fenced["tail"]
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
    paths = [
        private_path(config[name])
        for name in (
            "fullReportPath",
            "fullReportSignaturePath",
            "restoreEvidenceIndexPath",
            "fenceReportPath",
            "fenceReportSignaturePath",
            "fencedTailPath",
            "fencedTailSignaturePath",
        )
    ]
    command = [
        "dotnet",
        str(binary),
        "verify-fenced-tail",
        *(str(path) for path in paths),
        nonce,
    ]
    response = (
        bounded(command, 4096, 30)
        if product_runner is None
        else product_runner(command)
    )
    require(
        isinstance(response, bytes) and len(response) <= 4096,
        "product-recheck-response",
    )
    result = json.loads(response)
    require(
        isinstance(result, dict)
        and set(result)
        == {
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
        },
        "product-fenced-shape",
    )
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
    return {
        "publicationSha256": publication_sha,
        "syntheticOnly": root_key is not REVIEWED_PUBLICATION_ROOT_PEM
        or product_runner is not None
        or binary_path is not None,
    }
