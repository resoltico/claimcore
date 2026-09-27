"""Signed package publication root; owner config cannot choose this trust anchor."""

import base64
import hashlib
import json
import re
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

from deployment_common import canonical, require, timestamp, verify


def package_binary():
    return (
        Path(__file__).resolve().parents[2]
        / "artifacts/bin/ClaimCore.Database/release/ClaimCore.Database.dll"
    )


def package_publication_files():
    root = package_binary().parent
    return root / "claimcore-publication.json", root / "claimcore-publication.sig"


def _digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _uuid(value):
    try:
        import uuid

        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def verify_publication(root_key, manifest_path, signature_path, binary_path, now=None):
    now = now or datetime.now(timezone.utc)
    require(root_key is not None, "publication-root-unavailable")
    for path, maximum in ((manifest_path, 16384), (signature_path, 64)):
        require(
            path.is_file() and 0 < path.stat().st_size <= maximum, "publication-missing"
        )
    raw = manifest_path.read_bytes()
    signature = signature_path.read_bytes()
    require(len(signature) == 64, "publication-signature-shape")
    document = json.loads(raw)
    require(raw == canonical(document), "publication-not-canonical")
    envelope = {
        "report": document,
        "signatureBase64": base64.b64encode(signature).decode("ascii"),
    }
    if isinstance(root_key, bytes):
        with tempfile.TemporaryDirectory(prefix="claimcore-publish-root-") as temporary:
            key = Path(temporary) / "reviewed-root.pem"
            key.write_bytes(root_key)
            key.chmod(0o600)
            verified, digest = verify(envelope, key)
    else:
        verified, digest = verify(envelope, root_key)
    require(
        set(verified)
        == {
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
        },
        "publication-shape",
    )
    require(
        verified["format"] == "claimcore-publication-manifest-1", "publication-format"
    )
    for name in (
        "publicationId",
        "reportSignerKeyId",
        "checkpointSignerKeyId",
        "installationId",
        "lineageId",
    ):
        require(_uuid(verified[name]), "publication-identity")
    require(
        verified["reportSignerKeyId"] != verified["checkpointSignerKeyId"],
        "publication-key-separation",
    )
    for name in ("verifierBinarySha256", "witnessCutoffHash"):
        require(_digest(verified[name]), "publication-digest")
    require(
        type(verified["epoch"]) is int
        and verified["epoch"] > 0
        and type(verified["writerGeneration"]) is int
        and verified["writerGeneration"] > 0
        and type(verified["witnessCutoff"]) is int
        and verified["witnessCutoff"] >= 0,
        "publication-generation",
    )
    issued = timestamp(verified["issuedAt"])
    expires = timestamp(verified["validUntil"])
    require(
        issued <= now < expires <= issued + timedelta(days=7), "publication-expired"
    )
    require(binary_path.is_file(), "published-verifier-missing")
    require(binary_path.stat().st_size <= 100_000_000, "published-verifier-size")
    actual = hashlib.sha256(binary_path.read_bytes()).hexdigest()
    require(actual == verified["verifierBinarySha256"], "published-verifier-digest")
    return verified, digest


def match_qualification(publication, qualification, report_sha, config):
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
    require(_digest(report_sha), "qualification-digest")
