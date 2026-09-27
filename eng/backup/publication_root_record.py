"""Strict witnessed-root readback codec; only the fixed owner command may issue it."""

import base64
import hashlib
import json
import re
import subprocess
import tempfile
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from deployment_common import DeploymentRefusal, canonical, require, timestamp

FIELDS = {
    "format",
    "nonce",
    "installationId",
    "lineageId",
    "epoch",
    "rootId",
    "purpose",
    "publicKeyBase64",
    "publicKeySha256",
    "custodianActorId",
    "registrationCandidateBase64",
    "registrationCandidateSha256",
    "proofOfPossessionSignatureBase64",
    "registeredEventId",
    "registeredWitnessSequence",
    "registeredWitnessEpoch",
    "registeredWitnessHash",
    "authorityRevision",
    "approvals",
    "active",
    "retiredSequence",
    "registeredAt",
    "checkedAt",
    "validUntil",
}
CANDIDATE_FIELDS = {
    "format",
    "installationId",
    "lineageId",
    "epoch",
    "rootId",
    "publicKeySha256",
    "custodianActorId",
    "registeredEventId",
    "challengeNonce",
    "expectedAuthorityRevision",
    "approvalOneId",
    "approvalTwoId",
    "validUntil",
}
APPROVAL_FIELDS = {
    "approvalId",
    "actorId",
    "role",
    "grantRevision",
    "witnessSequence",
    "witnessEpoch",
    "witnessHash",
    "candidateSha256",
    "approvedAt",
    "validUntil",
}
SPKI_PREFIX = bytes.fromhex("302a300506032b6570032100")


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _base64(value, length, category):
    require(isinstance(value, str) and len(value) <= 16384, category)
    try:
        raw = base64.b64decode(value, validate=True)
    except (ValueError, base64.binascii.Error):
        raise DeploymentRefusal(category) from None
    require(
        len(raw) == length if length is not None else 0 < len(raw) <= 16384, category
    )
    return raw


def public_pem(raw_key):
    require(isinstance(raw_key, bytes) and len(raw_key) == 32, "root-key-shape")
    encoded = base64.b64encode(SPKI_PREFIX + raw_key).decode("ascii")
    return (
        "-----BEGIN PUBLIC KEY-----\n" + encoded + "\n-----END PUBLIC KEY-----\n"
    ).encode("ascii")


def _proof_of_possession(raw_key, candidate, signature):
    with tempfile.TemporaryDirectory(prefix="claimcore-root-pop-") as raw:
        root = Path(raw).resolve()
        root.chmod(0o700)
        key, statement, signed = (
            root / "root.pub",
            root / "candidate.json",
            root / "candidate.sig",
        )
        for path, data in (
            (key, public_pem(raw_key)),
            (statement, candidate),
            (signed, signature),
        ):
            path.write_bytes(data)
            path.chmod(0o600)
        result = subprocess.run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-rawin",
                "-pubin",
                "-inkey",
                str(key),
                "-in",
                str(statement),
                "-sigfile",
                str(signed),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(result.returncode == 0, "root-proof-of-possession")


def _approval(item, candidate_sha, epoch, sequence, registered_at):
    require(
        isinstance(item, dict) and set(item) == APPROVAL_FIELDS, "root-approval-shape"
    )
    require(
        _uuid(item["approvalId"])
        and _uuid(item["actorId"])
        and item["role"] in ("OWNER", "DATA_STEWARD")
        and type(item["grantRevision"]) is int
        and item["grantRevision"] > 0
        and type(item["witnessSequence"]) is int
        and 0 < item["witnessSequence"] < sequence
        and item["witnessEpoch"] == epoch
        and _sha(item["witnessHash"])
        and item["candidateSha256"] == candidate_sha,
        "root-approval-evidence",
    )
    approved, until = timestamp(item["approvedAt"]), timestamp(item["validUntil"])
    require(
        approved < until and registered_at <= until and approved <= registered_at,
        "root-approval-expiry",
    )


def parse_root_record(raw, nonce, expected, *, now=None):
    """Parse a fixed Database readback; this function cannot attest its witness source."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= 32768, "root-record-size")
    record = json.loads(raw)
    require(
        isinstance(record, dict) and raw == canonical(record), "root-record-canonical"
    )
    require(
        set(record) == FIELDS
        and record["format"] == "claimcore-publication-root-record-1",
        "root-record-shape",
    )
    require(record["nonce"] == nonce and _sha(nonce), "root-record-nonce")
    for name in ("installationId", "lineageId", "epoch"):
        require(record[name] == expected[name], "root-record-installation")
    for name in (
        "installationId",
        "lineageId",
        "rootId",
        "custodianActorId",
        "registeredEventId",
    ):
        require(_uuid(record[name]), "root-record-identity")
    require(
        record["purpose"] == "PUBLICATION_ROOT"
        and record["active"] is True
        and record["retiredSequence"] is None
        and type(record["epoch"]) is int
        and record["epoch"] > 0
        and type(record["authorityRevision"]) is int
        and record["authorityRevision"] > 0
        and type(record["registeredWitnessSequence"]) is int
        and record["registeredWitnessSequence"] > 0
        and record["registeredWitnessEpoch"] == record["epoch"]
        and _sha(record["registeredWitnessHash"]),
        "root-record-authority",
    )
    key = _base64(record["publicKeyBase64"], 32, "root-key-shape")
    require(
        hashlib.sha256(key).hexdigest() == record["publicKeySha256"], "root-key-digest"
    )
    candidate = _base64(
        record["registrationCandidateBase64"], None, "root-candidate-size"
    )
    source = json.loads(candidate)
    require(
        isinstance(source, dict) and candidate == canonical(source),
        "root-candidate-canonical",
    )
    require(
        set(source) == CANDIDATE_FIELDS
        and source["format"] == "claimcore-publication-root-registration-1",
        "root-candidate-shape",
    )
    require(
        hashlib.sha256(candidate).hexdigest() == record["registrationCandidateSha256"],
        "root-candidate-digest",
    )
    for name in (
        "installationId",
        "lineageId",
        "epoch",
        "rootId",
        "publicKeySha256",
        "custodianActorId",
        "registeredEventId",
    ):
        require(source[name] == record[name], "root-candidate-link")
    require(
        _sha(source["challengeNonce"])
        and source["expectedAuthorityRevision"] <= record["authorityRevision"],
        "root-candidate-authority",
    )
    signature = _base64(
        record["proofOfPossessionSignatureBase64"], 64, "root-pop-signature"
    )
    _proof_of_possession(key, candidate, signature)
    registered_at = timestamp(record["registeredAt"])
    checked, expires = timestamp(record["checkedAt"]), timestamp(record["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(
        registered_at <= checked <= now < expires <= checked + timedelta(minutes=5),
        "root-record-expired",
    )
    approvals = record["approvals"]
    require(isinstance(approvals, list) and len(approvals) == 2, "root-approval-count")
    for item in approvals:
        _approval(
            item,
            record["registrationCandidateSha256"],
            record["epoch"],
            record["registeredWitnessSequence"],
            registered_at,
        )
    require(
        len({item["actorId"] for item in approvals}) == 2
        and len({item["approvalId"] for item in approvals}) == 2
        and record["custodianActorId"] not in {item["actorId"] for item in approvals}
        and {source["approvalOneId"], source["approvalTwoId"]}
        == {item["approvalId"] for item in approvals},
        "root-approval-independence",
    )
    return record, public_pem(key)
