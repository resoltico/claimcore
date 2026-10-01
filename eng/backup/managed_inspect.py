"""Inspection of a retained backup cycle: signatures, checkpoints, attestations and restore."""

import json
import sys
import uuid
from datetime import UTC, datetime
from pathlib import Path

import inventory
from backup_types import JsonObject
from managed_common import (
    CLUSTERS,
    digest,
    metadata,
    private_path,
    require,
    utc_timestamp,
    verify_checkpoint_signature,
    verify_signature,
)
from managed_restore import restore_and_verify
from managed_wal import verify_wal_inventory

MANIFEST_LIMIT = 1024 * 1024
CHECKPOINT_LIMIT = 16 * 1024
ATTESTATION_LIMIT = 64 * 1024
CLOCK_SKEW_SECONDS = 300
CHECKPOINT_FORMAT = "claimcore-witness-checkpoint-1"
BARRIER_FIELDS = (
    "leaseId",
    "captureNonce",
    "writerGeneration",
    "backupCaptureSequence",
    "backupCaptureHash",
    "maintenanceEvidenceSha256",
)
BASE_FIELDS = (
    "copyId",
    "eventId",
    "postgresSystemId",
    "timeline",
    "walSegmentBytes",
    "backupManifestSha256",
    "walStartLsn",
    "walEndLsn",
    "ciphertextSha256",
    "ciphertextBytes",
    "locationCommitment",
    "custodianCommitment",
)


def _load_manifest(config: JsonObject, cycle_id: str) -> tuple[Path, Path, JsonObject]:
    uuid.UUID(cycle_id)
    cycle = private_path(config["archiveRoot"] / cycle_id, directory=True)
    source = cycle / "manifest.json"
    verify_signature(config, source, cycle / "manifest.sig")
    require(source.stat().st_size <= MANIFEST_LIMIT, "manifest-size")
    manifest = json.loads(source.read_bytes())
    require(
        manifest.get("format") == "claimcore-backup-cycle-1"
        and manifest.get("cycleId") == cycle_id,
        "manifest-invalid",
    )
    captured_at = utc_timestamp(manifest.get("capturedAt"))
    age_seconds = (datetime.now(UTC) - captured_at).total_seconds()
    require(
        -CLOCK_SKEW_SECONDS <= age_seconds <= config["maximumBackupAgeSeconds"], "backup-age-policy"
    )
    return cycle, source, manifest


def _load_tip(config: JsonObject, cycle_id: str, manifest: JsonObject) -> tuple[Path, JsonObject]:
    checkpoint: Path = config["checkpointRoot"] / (cycle_id + ".json")
    verify_checkpoint_signature(config, checkpoint, checkpoint.with_suffix(".sig"))
    require(checkpoint.stat().st_size <= CHECKPOINT_LIMIT, "checkpoint-size")
    require(digest(checkpoint) == manifest["checkpointSha256"], "checkpoint-manifest-digest")
    tip = json.loads(checkpoint.read_bytes())
    require(tip.get("format") == CHECKPOINT_FORMAT, "checkpoint-format")
    return checkpoint, tip


def _check_tip_binding(config: JsonObject, manifest: JsonObject, tip: JsonObject) -> None:
    require(
        manifest.get("copySigningKeyId") == config["signingKeyId"]
        and manifest.get("checkpointSigningKeyId") == config["checkpointSigningKeyId"]
        and tip.get("checkpointSigningKeyId") == config["checkpointSigningKeyId"],
        "checkpoint-key-identity",
    )
    require(
        tip.get("checkpointCustodianCommitment")
        == inventory.commitment(
            config["commitmentKey"].read_bytes(), "custodian", config["checkpointCustodianId"]
        ),
        "checkpoint-custodian-commitment",
    )
    require(tip.get("capturedAt") == manifest["capturedAt"], "checkpoint-time-mismatch")
    require(
        all(
            tip.get(field) == manifest.get(field)
            for field in ("cycleId", "installationId", "lineageId", "epoch")
        ),
        "checkpoint-identity",
    )
    require(
        tip.get("sequence") == manifest["witnessCheckpoint"]["sequence"]
        and tip.get("hash") == manifest["witnessCheckpoint"]["hash"],
        "checkpoint-tip",
    )
    for name in BARRIER_FIELDS:
        require(tip.get(name) == manifest.get(name), "checkpoint-barrier-binding")


def _compare_retained_checkpoints(config: JsonObject, tip: JsonObject) -> None:
    # A signed older backup cannot become authoritative merely because its
    # own manifest verifies. Compare every retained independent checkpoint.
    for candidate in config["checkpointRoot"].glob("*.json"):
        path = private_path(candidate)
        verify_checkpoint_signature(config, path, path.with_suffix(".sig"))
        other = json.loads(path.read_bytes())
        require(other.get("format") == CHECKPOINT_FORMAT, "checkpoint-format")
        require(
            other.get("installationId") == tip["installationId"]
            and other.get("lineageId") == tip["lineageId"],
            "checkpoint-identity",
        )
        require(other.get("epoch") <= tip["epoch"], "stale-backup-epoch")
        require(
            other.get("epoch") < tip["epoch"] or other.get("sequence") <= tip["sequence"],
            "stale-backup-tip",
        )
        require(
            other.get("epoch") != tip["epoch"]
            or other.get("sequence") != tip["sequence"]
            or other.get("hash") == tip["hash"],
            "checkpoint-divergence",
        )


def _check_live_witness(config: JsonObject, tip: JsonObject) -> None:
    live = metadata(config, "witness")
    require(
        live[0] == tip["installationId"] and live[1] == tip["lineageId"], "live-witness-identity"
    )
    require(
        int(live[2]) == tip["epoch"] and int(live[3]) == tip["sequence"] and live[4] == tip["hash"],
        "stale-or-divergent-witness-backup",
    )


def _check_record_identity(
    record: JsonObject, expected: tuple[str, str, str], tip: JsonObject
) -> None:
    cycle_id, cluster, copy_id = expected
    require(
        record.get("format") == "claimcore-managed-copy-attestation-1"
        and record.get("copyId") == copy_id
        and record.get("cycleId") == cycle_id,
        "attestation-identity",
    )
    require(
        record.get("cluster") == cluster
        and record.get("kind") == "BASE"
        and record.get("state") == "UNVERIFIED"
        and record.get("primaryRegistration") == "NOT_REGISTERED",
        "attestation-state",
    )
    require(
        record.get("installationId") == tip["installationId"]
        and record.get("lineageId") == tip["lineageId"]
        and record.get("epoch") == tip["epoch"],
        "attestation-installation",
    )
    require(
        record.get("witnessCutoffSequence") == tip["sequence"]
        and record.get("witnessCutoffHash") == tip["hash"],
        "attestation-cutoff",
    )


def _read_attestation(config: JsonObject, cycle_id: str, cluster: str, copy_id: str) -> JsonObject:
    record_path: Path = config["inventoryRoot"] / (
        cycle_id + "." + cluster + "." + copy_id + ".json"
    )
    verify_signature(config, record_path, record_path.with_suffix(".sig"))
    require(record_path.stat().st_size <= ATTESTATION_LIMIT, "attestation-size")
    record: JsonObject = json.loads(record_path.read_bytes())
    require(record_path.read_bytes() == inventory.canonical(record), "attestation-not-canonical")
    return record


def _check_base_attestations(
    config: JsonObject, cycle_id: str, manifest: JsonObject, tip: JsonObject
) -> dict[str, JsonObject]:
    attestations: dict[str, JsonObject] = {}
    for cluster in CLUSTERS:
        copy_id = manifest["copyIds"][cluster]
        uuid.UUID(copy_id)
        record = _read_attestation(config, cycle_id, cluster, copy_id)
        _check_record_identity(record, (cycle_id, cluster, copy_id), tip)
        require(
            record.get("ciphertextSha256") == manifest[cluster]["ciphertextSha256"]
            and record.get("ciphertextBytes") == manifest[cluster]["ciphertextBytes"],
            "attestation-ciphertext",
        )
        base = manifest["baseCopies"][cluster]
        require(
            isinstance(base, dict)
            and set(base) == set(BASE_FIELDS)
            and all(base[name] == record[name] for name in BASE_FIELDS),
            "manifest-copy-evidence",
        )
        require(
            record.get("signingKeyId") == config["signingKeyId"]
            and record.get("encryptionKeyId") == config["encryptionKeyId"],
            "attestation-key-identity",
        )
        require(
            utc_timestamp(record.get("retainUntil")) > datetime.now(UTC), "backup-retention-expired"
        )
        attestations[cluster] = record
    return attestations


def inspect(config: JsonObject, cycle_id: str, verifier: str) -> None:
    """Prove a retained cycle end to end and print its synthetic-only qualification."""
    cycle, manifest_file, manifest = _load_manifest(config, cycle_id)
    checkpoint, tip = _load_tip(config, cycle_id, manifest)
    _check_tip_binding(config, manifest, tip)
    _compare_retained_checkpoints(config, tip)
    _check_live_witness(config, tip)
    attestations = _check_base_attestations(config, cycle_id, manifest, tip)
    wal_count = verify_wal_inventory(config, attestations, tip)
    restore_and_verify(
        config, verifier, (manifest, attestations, tip), (cycle, manifest_file, checkpoint)
    )
    sys.stdout.write(
        (
            json.dumps(
                {
                    "status": "functional-restore-verified",
                    "cycleId": cycle_id,
                    "qualificationScope": "synthetic-only",
                    "promotionAuthorized": False,
                    "locallyAttestedWalCopies": wal_count,
                    "walFreshnessQualified": False,
                    "managedErasureDeletionProved": False,
                }
            )
        )
        + "\n"
    )
