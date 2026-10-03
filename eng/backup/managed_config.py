"""Owner-private managed backup configuration, validated before any command runs."""

import json
import os
import re
from pathlib import Path

from backup_types import JsonObject
from checkpoint_signer_policy import socket_directory
from deployment_common import is_uuid, public_key_identity
from managed_common import CLUSTERS, private_path, require
from pg_service_policy import validate as validate_pg_service

SECONDS_PER_DAY = 86400
TEN_YEARS_SECONDS = 10 * 365 * SECONDS_PER_DAY
MIN_CADENCE_SECONDS = 60
COMMITMENT_KEY_BYTES = 32
MIN_BACKUP_BYTES = 1024 * 1024
MAX_BACKUP_BYTES = 1024**4
MAX_TAR_ENTRIES = 10_000_000
MAX_WAL_COPIES = 100_000
AGE_RECIPIENT = r"age1[023456789acdefghjklmnpqrstuvwxyz]{58}"
NAME = r"[A-Za-z][A-Za-z0-9_-]{0,63}"
REMOTE_FIELDS = {"topologyFile", "topologySignatureFile", "knownHostsFile", "sshIdentityFile"}
REMOTE_TARGETS = (
    ("topologyFile", "checkpointSignerTopologyFile"),
    ("topologySignatureFile", "checkpointSignerTopologySignatureFile"),
    ("knownHostsFile", "checkpointKnownHostsFile"),
    ("sshIdentityFile", "checkpointSshIdentityFile"),
)


def _separate(first: Path, second: Path, category: str) -> None:
    require(not first.is_relative_to(second) and not second.is_relative_to(first), category)


def _storage(config: JsonObject, archive: Path) -> None:
    checkpoint = private_path(config["checkpointRoot"], directory=True)
    _separate(checkpoint, archive, "checkpoint-storage-not-separate")
    config["checkpointRoot"] = checkpoint
    inventory_root = private_path(config["inventoryRoot"], directory=True)
    _separate(inventory_root, archive, "inventory-storage-not-separate")
    config["inventoryRoot"] = inventory_root
    config["commitmentKey"] = private_path(config["commitmentKey"])
    descriptor = os.open(config["commitmentKey"], os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    with os.fdopen(descriptor, "rb") as stream:
        material = stream.read(COMMITMENT_KEY_BYTES + 1)
    require(len(material) == COMMITMENT_KEY_BYTES, "commitment-key-size")
    require(any(material), "commitment-key-material")


def _integer(value: object, minimum: int, maximum: int) -> bool:
    return type(value) is int and minimum <= value <= maximum


def _policy(config: JsonObject) -> None:
    for name in ("signingKeyId", "checkpointSigningKeyId", "encryptionKeyId"):
        require(is_uuid(config[name]), "backup-key-identity")
    require(
        config["signingKeyId"] != config["checkpointSigningKeyId"], "checkpoint-signer-separation"
    )
    for name in ("backupIntervalSeconds", "maximumBackupAgeSeconds", "restoreHorizonSeconds"):
        require(
            _integer(config[name], MIN_CADENCE_SECONDS, TEN_YEARS_SECONDS), "backup-cadence-policy"
        )
    for name in ("backupRetentionSeconds", "walRetentionSeconds", "checkpointRetentionSeconds"):
        require(_integer(config[name], SECONDS_PER_DAY, TEN_YEARS_SECONDS), "retention-policy")
    require(
        config["backupIntervalSeconds"]
        <= config["maximumBackupAgeSeconds"]
        <= config["restoreHorizonSeconds"],
        "backup-cadence-policy",
    )
    require(
        config["restoreHorizonSeconds"]
        <= min(config["backupRetentionSeconds"], config["walRetentionSeconds"]),
        "recovery-retention-policy",
    )
    require(
        config["checkpointRetentionSeconds"]
        > max(config["backupRetentionSeconds"], config["walRetentionSeconds"]),
        "checkpoint-retention-policy",
    )


def _limits(config: JsonObject) -> None:
    require(
        _integer(config["maxBackupBytes"], MIN_BACKUP_BYTES, MAX_BACKUP_BYTES), "backup-size-policy"
    )
    require(_integer(config["maxTarEntries"], 1, MAX_TAR_ENTRIES), "backup-entry-policy")
    require(_integer(config["maxWalCopies"], 1, MAX_WAL_COPIES), "wal-inventory-policy")


def _signer(config: JsonObject) -> None:
    config["signingKey"] = private_path(config["signingKey"])
    config["verificationKey"] = private_path(config["verificationKey"])
    config["checkpointVerificationKey"] = private_path(config["checkpointVerificationKey"])
    mode = config["checkpointSignerMode"]
    require(mode in ("LOCAL_SYNTHETIC", "REMOTE_SSH"), "checkpoint-signer-mode")
    if mode == "LOCAL_SYNTHETIC":
        require(config.get("checkpointSignerRemote") is None, "checkpoint-signer-mode")
        signer_socket = Path(config["checkpointSignerSocket"])
        require(signer_socket.is_absolute(), "checkpoint-signer-endpoint")
        socket_directory(signer_socket.parent)
        config["checkpointSignerSocket"] = signer_socket
    else:
        require(config.get("checkpointSignerSocket") is None, "checkpoint-signer-mode")
        remote = config["checkpointSignerRemote"]
        require(
            isinstance(remote, dict) and set(remote) == REMOTE_FIELDS, "checkpoint-remote-config"
        )
        for name, target in REMOTE_TARGETS:
            config[target] = private_path(remote[name])
    require(
        public_key_identity(config["verificationKey"])
        != public_key_identity(config["checkpointVerificationKey"]),
        "checkpoint-signer-separation",
    )


def _custodians(config: JsonObject) -> None:
    require(
        isinstance(config["checkpointCustodianId"], str)
        and re.fullmatch(NAME, config["checkpointCustodianId"]),
        "checkpoint-custodian",
    )
    for cluster in CLUSTERS:
        item = config[cluster]
        require(re.fullmatch(NAME, item["custodianId"]) is not None, "custodian-id")
        for name in ("metadataService", "replicationService"):
            require(re.fullmatch(NAME, item[name]) is not None, "service-name")
        require(
            config["checkpointCustodianId"] != item["custodianId"],
            "checkpoint-custodian-separation",
        )


def configuration(path: str | os.PathLike[str], *, archiver: bool = False) -> JsonObject:
    """Load and validate the managed backup configuration; an archiver sees only its subset."""
    config_path = private_path(path)
    descriptor = os.open(config_path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, "rb") as stream:
        config: JsonObject = json.load(stream)
    require(config.get("format") == "claimcore-managed-backup-1", "configuration-format")
    require("qualifiedVerifierSha256" not in config, "owner-selected-verifier-is-unsupported")
    archive = private_path(config["archiveRoot"], directory=True)
    require(re.fullmatch(AGE_RECIPIENT, config["ageRecipient"]) is not None, "recipient-format")
    config["archiveRoot"] = archive
    if archiver:
        require(
            set(config) == {"format", "archiveRoot", "ageRecipient"},
            "archiver-config-has-owner-material",
        )
        return config
    _storage(config, archive)
    _policy(config)
    _limits(config)
    _signer(config)
    _custodians(config)
    validate_pg_service(config)
    return config
