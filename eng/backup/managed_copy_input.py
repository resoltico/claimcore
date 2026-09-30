"""Exact input contract for one managed-copy physical verification."""

import re
from pathlib import Path

from backup_types import Json, JsonObject
from managed_copy_verification_io import (
    private_directory,
    private_file,
    require,
    sha_text,
    uuid_text,
)

MAX_INT64 = 9223372036854775806
MAX_INT32 = 2147483647
MIN_SEGMENT_BYTES = 1048576
MAX_SEGMENT_BYTES = 1073741824
MAX_CIPHERTEXT_BYTES = 1099511627776
MAX_TAR_ENTRIES = 1000000
KEY_LIMIT = 16384
FIELDS = {
    "format",
    "verificationEventId",
    "copyId",
    "copyEventId",
    "copyRevision",
    "archiveObjectId",
    "installationId",
    "lineageId",
    "witnessEpoch",
    "witnessCutoffSequence",
    "witnessCutoffHash",
    "cluster",
    "kind",
    "postgresSystemId",
    "timeline",
    "walSegmentBytes",
    "backupManifestSha256",
    "walStartLsn",
    "walEndLsn",
    "walSegment",
    "locationCommitment",
    "ciphertextSha256",
    "ciphertextBytes",
    "ciphertextFile",
    "archiveRoot",
    "ageIdentityFile",
    "verificationSigningKeyFile",
    "verifierSigningKeyId",
    "verifierHolderActorId",
    "outputProofFile",
    "outputSignatureFile",
    "privateScratchRoot",
    "maximumPlaintextBytes",
    "maximumTarEntries",
    "databaseOwnerRole",
    "databaseName",
}


def _integer(value: Json, minimum: int, maximum: int) -> int:
    require(type(value) is int and minimum <= value <= maximum, "INTEGER_REFUSED")
    return int(value)


def _name(value: Json) -> str:
    require(
        isinstance(value, str) and re.fullmatch(r"[a-z][a-z0-9_]{0,62}", value) is not None,
        "DATABASE_IDENTITY_REFUSED",
    )
    return str(value)


def _lsn(value: Json) -> str:
    require(
        isinstance(value, str) and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None,
        "WAL_RANGE_REFUSED",
    )
    return str(value)


def _kind_fields(config: JsonObject) -> None:
    if config["kind"] == "BASE":
        sha_text(config["backupManifestSha256"])
        _lsn(config["walStartLsn"])
        _lsn(config["walEndLsn"])
        require(config["walSegment"] is None, "COPY_KIND_REFUSED")
        _name(config["databaseOwnerRole"])
        _name(config["databaseName"])
    else:
        require(
            config["backupManifestSha256"] is None
            and config["walStartLsn"] is None
            and config["walEndLsn"] is None
            and config["databaseOwnerRole"] is None
            and config["databaseName"] is None
            and isinstance(config["walSegment"], str)
            and re.fullmatch(r"[0-9A-F]{24}", config["walSegment"]) is not None,
            "COPY_KIND_REFUSED",
        )
        require(
            int(config["walSegment"][:8], 16) == config["timeline"],
            "WAL_TIMELINE_REFUSED",
        )


def _check_identities_and_numbers(config: JsonObject) -> None:
    for name in (
        "verificationEventId",
        "copyId",
        "copyEventId",
        "archiveObjectId",
        "installationId",
        "lineageId",
        "verifierSigningKeyId",
        "verifierHolderActorId",
    ):
        uuid_text(config[name])
    for name in ("witnessCutoffHash", "locationCommitment", "ciphertextSha256"):
        sha_text(config[name])
    _integer(config["copyRevision"], 1, MAX_INT64)
    _integer(config["witnessEpoch"], 1, MAX_INT64)
    _integer(config["witnessCutoffSequence"], 0, MAX_INT64)
    _integer(config["timeline"], 1, MAX_INT32)
    segment_size = _integer(config["walSegmentBytes"], MIN_SEGMENT_BYTES, MAX_SEGMENT_BYTES)
    require(segment_size & (segment_size - 1) == 0, "WAL_SEGMENT_SIZE_REFUSED")
    _integer(config["ciphertextBytes"], 1, MAX_CIPHERTEXT_BYTES)
    _integer(config["maximumPlaintextBytes"], MIN_SEGMENT_BYTES, MAX_CIPHERTEXT_BYTES)
    _integer(config["maximumTarEntries"], 1, MAX_TAR_ENTRIES)
    require(
        config["cluster"] in ("PRIMARY", "WITNESS") and config["kind"] in ("BASE", "WAL"),
        "COPY_KIND_REFUSED",
    )
    require(
        isinstance(config["postgresSystemId"], str)
        and re.fullmatch(r"[0-9]{1,20}", config["postgresSystemId"]) is not None,
        "POSTGRES_IDENTITY_REFUSED",
    )


def _check_paths(config: JsonObject) -> None:
    archive = private_directory(config["archiveRoot"])
    object_file = private_file(config["ciphertextFile"], MAX_CIPHERTEXT_BYTES)
    require(object_file.is_relative_to(archive), "ARCHIVE_OBJECT_OUTSIDE_ROOT")
    for parent in object_file.parents:
        private_directory(str(parent))
        if parent == archive:
            break
    private_file(config["ageIdentityFile"], KEY_LIMIT)
    private_directory(str(Path(config["ageIdentityFile"]).parent))
    private_file(config["verificationSigningKeyFile"], KEY_LIMIT)
    private_directory(str(Path(config["verificationSigningKeyFile"]).parent))
    private_directory(config["privateScratchRoot"])
    proof = Path(config["outputProofFile"])
    signature = Path(config["outputSignatureFile"])
    require(
        proof != signature and proof.suffix == ".json" and signature.suffix == ".sig",
        "OUTPUT_IDENTITY_REFUSED",
    )
    private_directory(str(proof.parent))
    private_directory(str(signature.parent))
    require(not proof.exists() and not signature.exists(), "OUTPUT_ALREADY_EXISTS")


def validate(config: JsonObject) -> JsonObject:
    """Validate every field and path of the verification input; return it unchanged."""
    require(
        isinstance(config, dict)
        and set(config) == FIELDS
        and config["format"] == "claimcore-managed-copy-verification-input-1",
        "INPUT_SHAPE_REFUSED",
    )
    _check_identities_and_numbers(config)
    _kind_fields(config)
    _check_paths(config)
    return config
