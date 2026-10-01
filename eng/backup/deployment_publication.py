"""Signed package publication root; owner config cannot choose this trust anchor."""

import base64
import hashlib
import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
    require,
    timestamp,
    verify_root_signed,
)

MANIFEST_LIMIT = 16384
VERIFIER_LIMIT = 100_000_000
VALIDITY_DAYS = 7


def package_binary() -> Path:
    """Return the packaged product verifier path."""
    return (
        Path(__file__).resolve().parents[2]
        / "artifacts/bin/ClaimCore.Database/release/ClaimCore.Database.dll"
    )


def package_publication_files() -> tuple[Path, Path]:
    """Return the packaged publication manifest and signature paths."""
    root = package_binary().parent
    return root / "claimcore-publication.json", root / "claimcore-publication.sig"


PUBLICATION_FIELDS = {
    "format",
    "publicationId",
    "verifierBinarySha256",
    "reportSignerKeyId",
    "checkpointSignerKeyId",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "witnessCutoff",
    "witnessCutoffHash",
    "issuedAt",
    "validUntil",
}
IDENTITY_FIELDS = (
    "publicationId",
    "reportSignerKeyId",
    "checkpointSignerKeyId",
    "installationId",
    "lineageId",
)


def _check_shape(verified: JsonObject) -> None:
    require(set(verified) == PUBLICATION_FIELDS, "publication-shape")
    require(verified["format"] == "claimcore-publication-manifest-1", "publication-format")
    for name in IDENTITY_FIELDS:
        require(is_uuid(verified[name]), "publication-identity")
    require(
        verified["reportSignerKeyId"] != verified["checkpointSignerKeyId"],
        "publication-key-separation",
    )
    for name in ("verifierBinarySha256", "witnessCutoffHash"):
        require(is_sha256(verified[name]), "publication-digest")
    require(
        type(verified["epoch"]) is int
        and verified["epoch"] > 0
        and type(verified["writerGeneration"]) is int
        and verified["writerGeneration"] > 0
        and type(verified["witnessCutoff"]) is int
        and verified["witnessCutoff"] >= 0,
        "publication-generation",
    )


def _read_envelope(manifest_path: Path, signature_path: Path) -> JsonObject:
    for path, maximum in ((manifest_path, MANIFEST_LIMIT), (signature_path, SIGNATURE_BYTES)):
        require(path.is_file() and 0 < path.stat().st_size <= maximum, "publication-missing")
    raw = manifest_path.read_bytes()
    signature = signature_path.read_bytes()
    require(len(signature) == SIGNATURE_BYTES, "publication-signature-shape")
    document = json.loads(raw)
    require(raw == canonical(document), "publication-not-canonical")
    return {"report": document, "signatureBase64": base64.b64encode(signature).decode("ascii")}


def _check_binary(verified: JsonObject, binary_path: Path) -> None:
    require(binary_path.is_file(), "published-verifier-missing")
    require(binary_path.stat().st_size <= VERIFIER_LIMIT, "published-verifier-size")
    actual = hashlib.sha256(binary_path.read_bytes()).hexdigest()
    require(actual == verified["verifierBinarySha256"], "published-verifier-digest")


def verify_publication(
    root_key: bytes | str | Path | None,
    manifest_path: Path,
    signature_path: Path,
    binary_path: Path,
    now: datetime | None = None,
) -> tuple[JsonObject, str]:
    """Verify the root-signed publication manifest and the packaged verifier it names."""
    current = now or datetime.now(UTC)
    if root_key is None:
        msg = "publication-root-unavailable"
        raise DeploymentRefusalError(msg)
    verified, digest = verify_root_signed(root_key, _read_envelope(manifest_path, signature_path))
    _check_shape(verified)
    issued, expires = timestamp(verified["issuedAt"]), timestamp(verified["validUntil"])
    require(
        issued <= current < expires <= issued + timedelta(days=VALIDITY_DAYS),
        "publication-expired",
    )
    _check_binary(verified, binary_path)
    return verified, digest


def match_qualification(
    publication: JsonObject, qualification: JsonObject, report_sha: str, config: JsonObject
) -> None:
    """Require the publication to match the qualification, report and config."""
    require(
        publication["installationId"] == qualification["installationId"],
        "publication-installation",
    )
    require(
        publication["lineageId"] == qualification["lineageId"],
        "publication-installation",
    )
    require(publication["epoch"] == qualification["epoch"], "publication-installation")
    require(
        publication["witnessCutoff"] == qualification["witnessCutoff"],
        "publication-cutoff",
    )
    require(
        publication["witnessCutoffHash"] == qualification["witnessCutoffHash"],
        "publication-cutoff",
    )
    require(
        publication["verifierBinarySha256"] == config["productVerifierSha256"],
        "publication-verifier",
    )
    require(is_sha256(report_sha), "qualification-digest")
