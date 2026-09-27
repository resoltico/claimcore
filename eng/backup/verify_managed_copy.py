"""Fixed owner-only proof for one encrypted managed BASE or WAL copy."""

import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

from managed_copy_verification_io import (
    VerificationFailure,
    canonical,
    exact_json,
    hash_private,
    now_and_expiry,
    private_directory,
    private_file,
    read_private,
    require,
    sha_text,
    sign,
    uuid_text,
    write_new,
)
from managed_copy_verification_pg import decrypt, verify_base, verify_wal

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


def _integer(value, minimum, maximum):
    require(type(value) is int and minimum <= value <= maximum, "INTEGER_REFUSED")
    return value


def _name(value):
    require(
        isinstance(value, str)
        and re.fullmatch(r"[a-z][a-z0-9_]{0,62}", value) is not None,
        "DATABASE_IDENTITY_REFUSED",
    )
    return value


def _lsn(value):
    require(
        isinstance(value, str)
        and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None,
        "WAL_RANGE_REFUSED",
    )
    return value


def _kind_fields(config):
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


def validate(config):
    require(
        isinstance(config, dict)
        and set(config) == FIELDS
        and config["format"] == "claimcore-managed-copy-verification-input-1",
        "INPUT_SHAPE_REFUSED",
    )
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
    _integer(config["copyRevision"], 1, 9223372036854775806)
    _integer(config["witnessEpoch"], 1, 9223372036854775806)
    _integer(config["witnessCutoffSequence"], 0, 9223372036854775806)
    _integer(config["timeline"], 1, 2147483647)
    segment_size = _integer(config["walSegmentBytes"], 1048576, 1073741824)
    require(segment_size & (segment_size - 1) == 0, "WAL_SEGMENT_SIZE_REFUSED")
    _integer(config["ciphertextBytes"], 1, 1099511627776)
    _integer(config["maximumPlaintextBytes"], 1048576, 1099511627776)
    _integer(config["maximumTarEntries"], 1, 1000000)
    require(
        config["cluster"] in ("PRIMARY", "WITNESS")
        and config["kind"] in ("BASE", "WAL"),
        "COPY_KIND_REFUSED",
    )
    require(
        isinstance(config["postgresSystemId"], str)
        and re.fullmatch(r"[0-9]{1,20}", config["postgresSystemId"]) is not None,
        "POSTGRES_IDENTITY_REFUSED",
    )
    _kind_fields(config)
    archive = private_directory(config["archiveRoot"])
    object_file = private_file(config["ciphertextFile"], 1099511627776)
    require(object_file.is_relative_to(archive), "ARCHIVE_OBJECT_OUTSIDE_ROOT")
    for parent in object_file.parents:
        private_directory(str(parent))
        if parent == archive:
            break
    private_file(config["ageIdentityFile"], 16384)
    private_directory(str(Path(config["ageIdentityFile"]).parent))
    private_file(config["verificationSigningKeyFile"], 16384)
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
    return config


def proof_fields(config, plain_length, plain_sha, verified):
    checked_at, expires = now_and_expiry()
    result = {
        "format": "claimcore-managed-copy-physical-verification-1",
        "source": "ClaimCore.ManagedCopyVerifier",
        "purpose": "RESTORE_COPY_VERIFIER",
        "verificationEventId": config["verificationEventId"],
        "nonce": os.urandom(32).hex(),
        "copyId": config["copyId"],
        "copyEventId": config["copyEventId"],
        "copyRevision": config["copyRevision"],
        "archiveObjectId": config["archiveObjectId"],
        "installationId": config["installationId"],
        "lineageId": config["lineageId"],
        "witnessEpoch": config["witnessEpoch"],
        "witnessCutoffSequence": config["witnessCutoffSequence"],
        "witnessCutoffHash": config["witnessCutoffHash"],
        "cluster": config["cluster"],
        "kind": config["kind"],
        "postgresSystemId": config["postgresSystemId"],
        "timeline": config["timeline"],
        "walSegmentBytes": config["walSegmentBytes"],
        "backupManifestSha256": config["backupManifestSha256"],
        "walStartLsn": config["walStartLsn"],
        "walEndLsn": config["walEndLsn"],
        "walSegment": config["walSegment"],
        "locationCommitment": config["locationCommitment"],
        "ciphertextSha256": config["ciphertextSha256"],
        "ciphertextBytes": config["ciphertextBytes"],
        "decryptedSha256": plain_sha,
        "decryptedBytes": plain_length,
        "verifierSigningKeyId": config["verifierSigningKeyId"],
        "verifierHolderActorId": config["verifierHolderActorId"],
        "checkedAt": checked_at,
        "validUntil": expires,
        "ciphertextRehashed": True,
        "decrypted": True,
        "fullPairReady": False,
        "realDataReady": False,
    }
    result.update(verified)
    result["recoveredPostgresSystemId"] = (
        config["postgresSystemId"] if config["kind"] == "BASE" else None
    )
    result["recoveredTimeline"] = (
        config["timeline"] if config["kind"] == "BASE" else None
    )
    result["recoveredInstallationId"] = (
        config["installationId"] if config["kind"] == "BASE" else None
    )
    result["recoveredLineageId"] = (
        config["lineageId"] if config["kind"] == "BASE" else None
    )
    result["recoveredEpoch"] = (
        config["witnessEpoch"] if config["kind"] == "BASE" else None
    )
    return result


def execute(config):
    actual = hash_private(
        config["ciphertextFile"], config["ciphertextBytes"], 1099511627776
    )
    require(actual == config["ciphertextSha256"], "CIPHERTEXT_DIVERGED")
    with tempfile.TemporaryDirectory(
        prefix="claimcore-copy-proof-", dir=config["privateScratchRoot"]
    ) as scratch:
        work = Path(scratch)
        plain = work / ("base.tar" if config["kind"] == "BASE" else "segment.wal")
        length, digest = decrypt(config, plain)
        verified = (
            verify_base(config, plain, work)
            if config["kind"] == "BASE"
            else verify_wal(config, plain)
        )
        require(
            hash_private(
                config["ciphertextFile"], config["ciphertextBytes"], 1099511627776
            )
            == actual,
            "CIPHERTEXT_CHANGED",
        )
        body = canonical(proof_fields(config, length, digest, verified))
        write_new(config["outputProofFile"], body)
        detached = sign(config["verificationSigningKeyFile"], config["outputProofFile"])
        write_new(config["outputSignatureFile"], detached)
        return hashlib.sha256(body).hexdigest()


def main():
    try:
        require(len(sys.argv) == 2, "ARGUMENTS_REFUSED")
        private_directory(str(Path(sys.argv[1]).parent))
        config = exact_json(read_private(sys.argv[1], 16384))
        validate(config)
        digest = execute(config)
        print(
            json.dumps(
                {
                    "status": "PHYSICAL_COPY_VERIFIED",
                    "proofSha256": digest,
                    "fullPairReady": False,
                    "realDataReady": False,
                },
                sort_keys=True,
            )
        )
        return 0
    except VerificationFailure as error:
        print(
            json.dumps(
                {"status": "REFUSED", "reason": error.code, "realDataReady": False},
                sort_keys=True,
            ),
            file=sys.stderr,
        )
        return 3


def safe_error(_kind, _error, _traceback):
    print(
        json.dumps(
            {
                "status": "REFUSED",
                "reason": "INSPECTION_UNAVAILABLE",
                "realDataReady": False,
            },
            sort_keys=True,
        ),
        file=sys.stderr,
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    raise SystemExit(main())
