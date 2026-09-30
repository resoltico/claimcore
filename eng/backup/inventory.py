"""Canonical owner-local managed-copy attestations; no database authority."""

import hashlib
import hmac
import json
import re
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import Json, JsonObject

PG_RANGE = re.compile(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}")
SYSTEM_IDENTIFIER = re.compile(r"[0-9]{1,20}")
ARCHIVE_SKEW_MINUTES = 5
TIMELINE_HEX_DIGITS = 8
BACKUP_MANIFEST_VERSION = 2


@dataclass(frozen=True)
class CopySource:
    """Who registers a copy, under which witness cutoff, and how its ciphertext is hashed."""

    config: JsonObject
    cluster: str
    witness: Sequence[str]
    digest: Callable[[Path | str], str]


def canonical(value: Json) -> bytes:
    """Return the canonical newline-terminated JSON encoding."""
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
    ).encode("ascii")


def commitment(key: bytes, label: str, value: str) -> str:
    """Return the HMAC commitment of `value` under `label`."""
    return hmac.new(key, (label + "\0" + value).encode("utf-8"), hashlib.sha256).hexdigest()


def utc_time(instant: datetime) -> str:
    """Return `instant` as second-precision UTC text."""
    return instant.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")


def require_pg_range(value: str) -> str:
    """Return `value` when it is a PostgreSQL LSN, otherwise raise."""
    if not PG_RANGE.fullmatch(value):
        msg = "invalid PostgreSQL WAL range"
        raise ValueError(msg)
    return value


def _attestation(
    source: CopySource,
    ciphertext: Path | str,
    captured: datetime,
    retention_seconds: int,
    specific: JsonObject,
) -> JsonObject:
    config, cluster, witness = source.config, source.cluster, source.witness
    key = config["commitmentKey"].read_bytes()
    destination = str(Path(ciphertext).resolve(strict=True))
    return {
        "format": "claimcore-managed-copy-attestation-1",
        "eventId": str(uuid.uuid4()),
        "copyId": str(uuid.uuid4()),
        "copyRevision": 1,
        "eventKind": "REGISTER",
        "previousEventHash": None,
        "installationId": witness[0],
        "lineageId": witness[1],
        "epoch": int(witness[2]),
        "cluster": cluster,
        "sourceCaseId": None,
        "witnessCutoffSequence": int(witness[3]),
        "witnessCutoffHash": witness[4],
        "ciphertextSha256": source.digest(ciphertext),
        "ciphertextBytes": Path(ciphertext).stat().st_size,
        "encryptionKeyId": config["encryptionKeyId"],
        "signingKeyId": config["signingKeyId"],
        "custodianCommitment": commitment(key, "custodian", config[cluster]["custodianId"]),
        "locationCommitment": commitment(key, "location", destination),
        "capturedAt": utc_time(captured),
        "retainUntil": utc_time(captured + timedelta(seconds=retention_seconds)),
        "lastVerifiedAt": None,
        "deletionProofSha256": None,
        "verificationProofSha256": None,
        "state": "UNVERIFIED",
        "primaryRegistration": "NOT_REGISTERED",
        **specific,
    }


def base_copy(
    source: CopySource,
    cycle_id: str,
    ciphertext: Path | str,
    pg_manifest: JsonObject,
    segment_bytes: int,
) -> JsonObject:
    """Return the REGISTER attestation of an encrypted base backup."""
    ranges = pg_manifest.get("WAL-Ranges")
    if (
        pg_manifest.get("PostgreSQL-Backup-Manifest-Version") != BACKUP_MANIFEST_VERSION
        or not isinstance(ranges, list)
        or len(ranges) != 1
    ):
        msg = "unsupported backup manifest"
        raise ValueError(msg)
    wal_range = ranges[0]
    timeline = wal_range.get("Timeline")
    if type(timeline) is not int or timeline <= 0:
        msg = "invalid backup timeline"
        raise ValueError(msg)
    system = str(pg_manifest.get("System-Identifier"))
    if not SYSTEM_IDENTIFIER.fullmatch(system):
        msg = "invalid PostgreSQL system identifier"
        raise ValueError(msg)
    return _attestation(
        source,
        ciphertext,
        datetime.now(UTC),
        source.config["backupRetentionSeconds"],
        {
            "kind": "BASE",
            "postgresSystemId": system,
            "timeline": timeline,
            "walSegmentBytes": segment_bytes,
            "backupManifestSha256": pg_manifest["_sha256"],
            "walStartLsn": require_pg_range(wal_range.get("Start-LSN")),
            "walEndLsn": require_pg_range(wal_range.get("End-LSN")),
            "walSegment": None,
            "cycleId": cycle_id,
        },
    )


def wal_copy(
    source: CopySource,
    segment: str,
    ciphertext: Path | str,
    record: JsonObject,
    postgres_system: str,
    *,
    segment_bytes: int,
) -> JsonObject:
    """Return the REGISTER attestation of an archived WAL segment."""
    now = datetime.fromisoformat(record["archivedAt"])
    if now.tzinfo is None or now > datetime.now(UTC) + timedelta(minutes=ARCHIVE_SKEW_MINUTES):
        msg = "invalid archive timestamp"
        raise ValueError(msg)
    timeline = int(segment[:TIMELINE_HEX_DIGITS], 16)
    if timeline <= 0 or record["sourceSha256"] is None:
        msg = "invalid WAL segment metadata"
        raise ValueError(msg)
    return _attestation(
        source,
        ciphertext,
        now,
        source.config["walRetentionSeconds"],
        {
            "kind": "WAL",
            "postgresSystemId": postgres_system,
            "timeline": timeline,
            "walSegmentBytes": segment_bytes,
            "backupManifestSha256": None,
            "walStartLsn": None,
            "walEndLsn": None,
            "walSegment": segment,
            "cycleId": None,
        },
    )
