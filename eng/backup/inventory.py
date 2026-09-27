"""Canonical owner-local managed-copy attestations; no database authority."""

import hashlib
import hmac
import json
import re
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path


def canonical(value):
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")


def commitment(key, label, value):
    return hmac.new(
        key, (label + "\0" + value).encode("utf-8"), hashlib.sha256
    ).hexdigest()


def utc_time(instant):
    return (
        instant.astimezone(timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )


def require_pg_range(value):
    if not re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value):
        raise ValueError("invalid PostgreSQL WAL range")
    return value


def base_copy(
    config, cluster, cycle_id, ciphertext, pg_manifest, segment_bytes, witness, digest
):
    ranges = pg_manifest.get("WAL-Ranges")
    if (
        pg_manifest.get("PostgreSQL-Backup-Manifest-Version") != 2
        or not isinstance(ranges, list)
        or len(ranges) != 1
    ):
        raise ValueError("unsupported backup manifest")
    wal_range = ranges[0]
    timeline = wal_range.get("Timeline")
    if type(timeline) is not int or timeline <= 0:
        raise ValueError("invalid backup timeline")
    system = str(pg_manifest.get("System-Identifier"))
    if not re.fullmatch(r"[0-9]{1,20}", system):
        raise ValueError("invalid PostgreSQL system identifier")
    copy_id = str(uuid.uuid4())
    now = datetime.now(timezone.utc)
    key = config["commitmentKey"].read_bytes()
    destination = str(Path(ciphertext).resolve(strict=True))
    return {
        "format": "claimcore-managed-copy-attestation-1",
        "eventId": str(uuid.uuid4()),
        "copyId": copy_id,
        "copyRevision": 1,
        "eventKind": "REGISTER",
        "previousEventHash": None,
        "installationId": witness[0],
        "lineageId": witness[1],
        "epoch": int(witness[2]),
        "cluster": cluster,
        "kind": "BASE",
        "sourceCaseId": None,
        "postgresSystemId": system,
        "timeline": timeline,
        "walSegmentBytes": segment_bytes,
        "backupManifestSha256": pg_manifest["_sha256"],
        "walStartLsn": require_pg_range(wal_range.get("Start-LSN")),
        "walEndLsn": require_pg_range(wal_range.get("End-LSN")),
        "walSegment": None,
        "witnessCutoffSequence": int(witness[3]),
        "witnessCutoffHash": witness[4],
        "ciphertextSha256": digest(ciphertext),
        "ciphertextBytes": Path(ciphertext).stat().st_size,
        "encryptionKeyId": config["encryptionKeyId"],
        "signingKeyId": config["signingKeyId"],
        "custodianCommitment": commitment(
            key, "custodian", config[cluster]["custodianId"]
        ),
        "locationCommitment": commitment(key, "location", destination),
        "capturedAt": utc_time(now),
        "retainUntil": utc_time(
            now + timedelta(seconds=config["backupRetentionSeconds"])
        ),
        "lastVerifiedAt": None,
        "deletionProofSha256": None,
        "verificationProofSha256": None,
        "state": "UNVERIFIED",
        "primaryRegistration": "NOT_REGISTERED",
        "cycleId": cycle_id,
    }


def wal_copy(
    config,
    cluster,
    segment,
    ciphertext,
    record,
    postgres_system,
    segment_bytes,
    witness,
    digest,
):
    copy_id = str(uuid.uuid4())
    now = datetime.fromisoformat(record["archivedAt"].replace("Z", "+00:00"))
    if now.tzinfo is None or now > datetime.now(timezone.utc) + timedelta(minutes=5):
        raise ValueError("invalid archive timestamp")
    key = config["commitmentKey"].read_bytes()
    destination = str(Path(ciphertext).resolve(strict=True))
    timeline = int(segment[:8], 16)
    if timeline <= 0 or record["sourceSha256"] is None:
        raise ValueError("invalid WAL segment metadata")
    return {
        "format": "claimcore-managed-copy-attestation-1",
        "eventId": str(uuid.uuid4()),
        "copyId": copy_id,
        "copyRevision": 1,
        "eventKind": "REGISTER",
        "previousEventHash": None,
        "installationId": witness[0],
        "lineageId": witness[1],
        "epoch": int(witness[2]),
        "cluster": cluster,
        "kind": "WAL",
        "sourceCaseId": None,
        "postgresSystemId": postgres_system,
        "timeline": timeline,
        "walSegmentBytes": segment_bytes,
        "backupManifestSha256": None,
        "walStartLsn": None,
        "walEndLsn": None,
        "walSegment": segment,
        "witnessCutoffSequence": int(witness[3]),
        "witnessCutoffHash": witness[4],
        "ciphertextSha256": digest(ciphertext),
        "ciphertextBytes": Path(ciphertext).stat().st_size,
        "encryptionKeyId": config["encryptionKeyId"],
        "signingKeyId": config["signingKeyId"],
        "custodianCommitment": commitment(
            key, "custodian", config[cluster]["custodianId"]
        ),
        "locationCommitment": commitment(key, "location", destination),
        "capturedAt": utc_time(now),
        "retainUntil": utc_time(now + timedelta(seconds=config["walRetentionSeconds"])),
        "lastVerifiedAt": None,
        "deletionProofSha256": None,
        "verificationProofSha256": None,
        "state": "UNVERIFIED",
        "primaryRegistration": "NOT_REGISTERED",
        "cycleId": None,
    }
