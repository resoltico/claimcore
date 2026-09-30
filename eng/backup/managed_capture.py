"""One captured backup cycle: encrypted base copies, signed checkpoint, manifest, attestations."""

import json
import shutil
import sys
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import overload

import inventory
from backup_types import JsonObject
from checkpoint_signer_client import sign_checkpoint
from managed_basebackup import capture_one, postgres_control, read_pg_manifest
from managed_common import (
    CLUSTERS,
    digest,
    json_bytes,
    private_path,
    require,
    sign,
    sync_directory,
    write_new,
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
IDENTITY_NAMES = ("installation", "lineage", "epoch")
BARRIER_SOURCES = {
    "leaseId": "leaseId",
    "captureNonce": "nonce",
    "writerGeneration": "writerGeneration",
    "backupCaptureSequence": "cutoffSequence",
    "backupCaptureHash": "cutoffHash",
    "maintenanceEvidenceSha256": "maintenanceEvidenceSha256",
}


def save_attestation(config: JsonObject, attestation: JsonObject) -> Path:
    """Write and sign a managed-copy attestation, removing it again if signing fails."""
    copy_id = attestation["copyId"]
    uuid.UUID(copy_id)
    prefix = attestation["cycleId"] or ("wal." + attestation["walSegment"])
    target: Path = config["inventoryRoot"] / (
        prefix + "." + attestation["cluster"] + "." + copy_id + ".json"
    )
    write_new(target, inventory.canonical(attestation))
    try:
        sync_directory(config["inventoryRoot"])
        sign(config, target, target.with_suffix(".sig"))
    except Exception:
        target.unlink(missing_ok=True)
        raise
    return target


def _open_cycle(config: JsonObject, held: JsonObject | None) -> tuple[Path, str]:
    archive = config["archiveRoot"]
    if held is None:
        cycle_id = str(uuid.uuid4())
        cycle = archive / cycle_id
        cycle.mkdir(mode=0o700)
        return cycle, cycle_id
    cycle = private_path(held["cycleRoot"], directory=True)
    require(cycle.parent == archive and not any(cycle.iterdir()), "held-cycle-root")
    require(str(uuid.UUID(cycle.name)) == cycle.name, "held-cycle-id")
    return cycle, cycle.name


def _check_identities(
    details: dict[str, JsonObject], held: JsonObject | None
) -> tuple[list[str], list[str]]:
    primary = details["primary"]["after"]
    witness = details["witness"]["after"]
    for index, name in enumerate(IDENTITY_NAMES):
        require(primary[index] == witness[index], "cluster-identity-mismatch-" + name)
    if held is not None:
        require(
            primary[0] == held["installationId"]
            and primary[1] == held["lineageId"]
            and int(primary[2]) == held["epoch"]
            and int(witness[3]) == held["cutoffSequence"]
            and witness[4] == held["cutoffHash"],
            "held-capture-cutoff",
        )
    return primary, witness


def _attest_base(
    config: JsonObject, cycle: Path, witness: list[str], held: JsonObject | None
) -> dict[str, JsonObject]:
    attestations: dict[str, JsonObject] = {}
    for cluster in CLUSTERS:
        ciphertext = cycle / (cluster + ".tar.age")
        pg_manifest = read_pg_manifest(config, ciphertext)
        system_id, timeline, segment_bytes = postgres_control(config, cluster)
        require(str(pg_manifest.get("System-Identifier")) == system_id, "backup-system-changed")
        require(pg_manifest["WAL-Ranges"][0]["Timeline"] == timeline, "backup-timeline-changed")
        if held is not None:
            require(
                system_id == held[cluster + "SystemId"] and timeline == held[cluster + "Timeline"],
                "held-cluster-identity",
            )
        source = inventory.CopySource(config, cluster, witness, digest)
        attestations[cluster] = inventory.base_copy(
            source, cycle.name, ciphertext, pg_manifest, segment_bytes
        )
    return attestations


def _barrier_fields(held: JsonObject | None) -> JsonObject:
    return {
        name: (held[source] if held is not None else None)
        for name, source in BARRIER_SOURCES.items()
    }


def _write_checkpoint(
    config: JsonObject,
    cycle_id: str,
    identity: tuple[list[str], list[str]],
    captured_at: str,
    barrier: JsonObject,
) -> Path:
    primary, witness = identity
    checkpoint_file: Path = config["checkpointRoot"] / (cycle_id + ".json")
    body = {
        "format": "claimcore-witness-checkpoint-1",
        "cycleId": cycle_id,
        "installationId": primary[0],
        "lineageId": primary[1],
        "epoch": int(primary[2]),
        "sequence": int(witness[3]),
        "hash": witness[4],
        "capturedAt": captured_at,
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "checkpointCustodianCommitment": inventory.commitment(
            config["commitmentKey"].read_bytes(), "custodian", config["checkpointCustodianId"]
        ),
        **barrier,
    }
    write_new(checkpoint_file, json_bytes(body))
    sync_directory(config["checkpointRoot"])
    return checkpoint_file


def _manifest(
    config: JsonObject,
    cycle_id: str,
    details: dict[str, JsonObject],
    attestations: dict[str, JsonObject],
    extra: JsonObject,
) -> JsonObject:
    primary, witness = details["primary"]["after"], details["witness"]["after"]
    return {
        "format": "claimcore-backup-cycle-1",
        "cycleId": cycle_id,
        "consistencyScope": "unfenced-capture",
        "capturedAt": extra["capturedAt"],
        "installationId": primary[0],
        "lineageId": primary[1],
        "epoch": int(primary[2]),
        "primary": details["primary"],
        "witness": details["witness"],
        "witnessCheckpoint": {"sequence": int(witness[3]), "hash": witness[4]},
        "copyIds": {cluster: attestations[cluster]["copyId"] for cluster in CLUSTERS},
        "baseCopies": {
            cluster: {name: attestations[cluster][name] for name in BASE_FIELDS}
            for cluster in CLUSTERS
        },
        "copySigningKeyId": config["signingKeyId"],
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "checkpointSha256": extra["checkpointSha256"],
        **extra["barrier"],
    }


def _seal_cycle(
    config: JsonObject,
    cycle: Path,
    details: dict[str, JsonObject],
    held: JsonObject | None,
    created: list[Path],
) -> JsonObject:
    primary, witness = _check_identities(details, held)
    attestations = _attest_base(config, cycle, witness, held)
    captured_at = inventory.utc_time(datetime.now(UTC))
    barrier = _barrier_fields(held)
    checkpoint_file = _write_checkpoint(
        config, cycle.name, (primary, witness), captured_at, barrier
    )
    created.append(checkpoint_file)
    sign_checkpoint(config, checkpoint_file, checkpoint_file.with_suffix(".sig"))
    created.append(checkpoint_file.with_suffix(".sig"))
    manifest = _manifest(
        config,
        cycle.name,
        details,
        attestations,
        {
            "capturedAt": captured_at,
            "checkpointSha256": digest(checkpoint_file),
            "barrier": barrier,
        },
    )
    write_new(cycle / "manifest.json", json_bytes(manifest))
    sync_directory(cycle)
    sign(config, cycle / "manifest.json", cycle / "manifest.sig")
    # Distinct local files/keys do not prove independent custody.
    # The owner receipt and post-capture REGISTER/VERIFY are separate.
    for cluster in CLUSTERS:
        saved = save_attestation(config, attestations[cluster])
        created.extend((saved, saved.with_suffix(".sig")))
    return {
        "primaryCiphertextPath": str(cycle / "primary.tar.age"),
        "witnessCiphertextPath": str(cycle / "witness.tar.age"),
        "checkpointPath": str(checkpoint_file),
        "cycleManifestPath": str(cycle / "manifest.json"),
        "cycleManifestSignaturePath": str(cycle / "manifest.sig"),
    }


@overload
def capture(config: JsonObject, held: JsonObject) -> JsonObject: ...


@overload
def capture(config: JsonObject, held: None = None) -> None: ...


def capture(config: JsonObject, held: JsonObject | None = None) -> JsonObject | None:
    """Capture one backup cycle; under a held lease return the paths the owner must seal."""
    cycle, cycle_id = _open_cycle(config, held)
    created: list[Path] = []
    try:
        details = {cluster: capture_one(config, cluster, cycle) for cluster in CLUSTERS}
        paths = _seal_cycle(config, cycle, details, held, created)
    except Exception:
        # Preserve held-cycle material for owner reconciliation; unfenced fresh
        # scratch may be removed only before any owner FINISH was attempted.
        if held is None:
            for item in reversed(created):
                item.unlink(missing_ok=True)
            shutil.rmtree(cycle)
        raise
    if held is None:
        sys.stdout.write(json.dumps({"status": "captured", "cycleId": cycle_id}) + "\n")
        return None
    return paths
