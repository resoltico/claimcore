"""Strict witnessed-root readback codec; only the fixed owner command may issue it."""

import base64
import hashlib
import json
import subprocess
import tempfile
from binascii import Error as Base64Error
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import Json, JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
    require,
    timestamp,
)

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


RECORD_LIMIT = 32768
ENCODED_LIMIT = 16384
RAW_KEY_BYTES = 32
VALIDITY_MINUTES = 5
APPROVAL_COUNT = 2
BOUND_FIELDS = (
    "installationId",
    "lineageId",
    "epoch",
    "rootId",
    "publicKeySha256",
    "custodianActorId",
    "registeredEventId",
)


def _base64(value: Json, length: int | None, category: str) -> bytes:
    require(isinstance(value, str) and len(value) <= ENCODED_LIMIT, category)
    try:
        raw = base64.b64decode(value, validate=True)
    except (ValueError, Base64Error):
        raise DeploymentRefusalError(category) from None
    require(len(raw) == length if length is not None else 0 < len(raw) <= ENCODED_LIMIT, category)
    return raw


def public_pem(raw_key: bytes) -> bytes:
    """Return the PEM public key for a raw Ed25519 key."""
    require(isinstance(raw_key, bytes) and len(raw_key) == RAW_KEY_BYTES, "root-key-shape")
    encoded = base64.b64encode(SPKI_PREFIX + raw_key).decode("ascii")
    return ("-----BEGIN PUBLIC KEY-----\n" + encoded + "\n-----END PUBLIC KEY-----\n").encode(
        "ascii"
    )


def _proof_of_possession(raw_key: bytes, candidate: bytes, signature: bytes) -> None:
    with tempfile.TemporaryDirectory(prefix="claimcore-root-pop-") as raw:
        root = Path(raw).resolve()
        root.chmod(0o700)
        key, statement, signed = (
            root / "root.pub",
            root / "candidate.json",
            root / "candidate.sig",
        )
        for path, data in ((key, public_pem(raw_key)), (statement, candidate), (signed, signature)):
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


def _approval(
    item: JsonObject, candidate_sha: str, witness: tuple[int, int], registered_at: datetime
) -> None:
    epoch, sequence = witness
    require(isinstance(item, dict) and set(item) == APPROVAL_FIELDS, "root-approval-shape")
    require(
        is_uuid(item["approvalId"])
        and is_uuid(item["actorId"])
        and item["role"] in ("OWNER", "DATA_STEWARD")
        and type(item["grantRevision"]) is int
        and item["grantRevision"] > 0
        and type(item["witnessSequence"]) is int
        and 0 < item["witnessSequence"] < sequence
        and item["witnessEpoch"] == epoch
        and is_sha256(item["witnessHash"])
        and item["candidateSha256"] == candidate_sha,
        "root-approval-evidence",
    )
    approved, until = timestamp(item["approvedAt"]), timestamp(item["validUntil"])
    require(
        approved < until and registered_at <= until and approved <= registered_at,
        "root-approval-expiry",
    )


def _check_record(record: JsonObject, nonce: str, expected: JsonObject) -> None:
    require(
        set(record) == FIELDS and record["format"] == "claimcore-publication-root-record-1",
        "root-record-shape",
    )
    require(record["nonce"] == nonce and is_sha256(nonce), "root-record-nonce")
    for name in ("installationId", "lineageId", "epoch"):
        require(record[name] == expected[name], "root-record-installation")
    for name in ("installationId", "lineageId", "rootId", "custodianActorId", "registeredEventId"):
        require(is_uuid(record[name]), "root-record-identity")
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
        and is_sha256(record["registeredWitnessHash"]),
        "root-record-authority",
    )


def _check_candidate(record: JsonObject, key: bytes) -> tuple[bytes, JsonObject]:
    require(hashlib.sha256(key).hexdigest() == record["publicKeySha256"], "root-key-digest")
    candidate = _base64(record["registrationCandidateBase64"], None, "root-candidate-size")
    source = json.loads(candidate)
    require(isinstance(source, dict) and candidate == canonical(source), "root-candidate-canonical")
    require(
        set(source) == CANDIDATE_FIELDS
        and source["format"] == "claimcore-publication-root-registration-1",
        "root-candidate-shape",
    )
    require(
        hashlib.sha256(candidate).hexdigest() == record["registrationCandidateSha256"],
        "root-candidate-digest",
    )
    for name in BOUND_FIELDS:
        require(source[name] == record[name], "root-candidate-link")
    require(
        is_sha256(source["challengeNonce"])
        and source["expectedAuthorityRevision"] <= record["authorityRevision"],
        "root-candidate-authority",
    )
    return candidate, source


def _check_approvals(record: JsonObject, source: JsonObject, now: datetime | None) -> None:
    registered_at = timestamp(record["registeredAt"])
    checked, expires = timestamp(record["checkedAt"]), timestamp(record["validUntil"])
    current = now or datetime.now(UTC)
    require(
        registered_at
        <= checked
        <= current
        < expires
        <= checked + timedelta(minutes=VALIDITY_MINUTES),
        "root-record-expired",
    )
    approvals = record["approvals"]
    require(isinstance(approvals, list) and len(approvals) == APPROVAL_COUNT, "root-approval-count")
    for item in approvals:
        _approval(
            item,
            record["registrationCandidateSha256"],
            (record["epoch"], record["registeredWitnessSequence"]),
            registered_at,
        )
    require(
        len({item["actorId"] for item in approvals}) == APPROVAL_COUNT
        and len({item["approvalId"] for item in approvals}) == APPROVAL_COUNT
        and record["custodianActorId"] not in {item["actorId"] for item in approvals}
        and {source["approvalOneId"], source["approvalTwoId"]}
        == {item["approvalId"] for item in approvals},
        "root-approval-independence",
    )


def parse_root_record(
    raw: bytes, nonce: str, expected: JsonObject, *, now: datetime | None = None
) -> tuple[JsonObject, bytes]:
    """Parse a fixed Database readback; this function cannot attest its witness source."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= RECORD_LIMIT, "root-record-size")
    record = json.loads(raw)
    require(isinstance(record, dict) and raw == canonical(record), "root-record-canonical")
    _check_record(record, nonce, expected)
    key = _base64(record["publicKeyBase64"], RAW_KEY_BYTES, "root-key-shape")
    candidate, source = _check_candidate(record, key)
    signature = _base64(
        record["proofOfPossessionSignatureBase64"], SIGNATURE_BYTES, "root-pop-signature"
    )
    _proof_of_possession(key, candidate, signature)
    _check_approvals(record, source, now)
    return record, public_pem(key)
