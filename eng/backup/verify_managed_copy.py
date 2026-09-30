"""Fixed owner-only proof for one encrypted managed BASE or WAL copy."""

import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path
from types import TracebackType

from backup_types import JsonObject
from managed_copy_input import FIELDS, validate
from managed_copy_source import decrypt
from managed_copy_verification_io import (
    VerificationFailureError,
    canonical,
    exact_json,
    hash_private,
    now_and_expiry,
    private_directory,
    read_private,
    require,
    sign,
    write_new,
)
from managed_copy_verification_pg import verify_base, verify_wal

__all__ = ["FIELDS"]

CIPHERTEXT_LIMIT = 1099511627776
CONFIG_LIMIT = 16384
NONCE_BYTES = 32
EXPECTED_ARGUMENTS = 2
REFUSED_EXIT = 3

COPIED = (
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
    "verifierSigningKeyId",
    "verifierHolderActorId",
)
RECOVERED = {
    "recoveredPostgresSystemId": "postgresSystemId",
    "recoveredTimeline": "timeline",
    "recoveredInstallationId": "installationId",
    "recoveredLineageId": "lineageId",
    "recoveredEpoch": "witnessEpoch",
}


def proof_fields(
    config: JsonObject, plain_length: int, plain_sha: str, verified: JsonObject
) -> JsonObject:
    """Assemble the unsigned physical-verification proof for the verified copy."""
    checked_at, expires = now_and_expiry()
    result: JsonObject = {
        "format": "claimcore-managed-copy-physical-verification-1",
        "source": "ClaimCore.ManagedCopyVerifier",
        "purpose": "RESTORE_COPY_VERIFIER",
        "nonce": os.urandom(NONCE_BYTES).hex(),
        **{name: config[name] for name in COPIED},
        "decryptedSha256": plain_sha,
        "decryptedBytes": plain_length,
        "checkedAt": checked_at,
        "validUntil": expires,
        "ciphertextRehashed": True,
        "decrypted": True,
        "fullPairReady": False,
        "realDataReady": False,
    }
    result.update(verified)
    base = config["kind"] == "BASE"
    for name, source in RECOVERED.items():
        result[name] = config[source] if base else None
    return result


def execute(config: JsonObject) -> str:
    """Verify the copy, then write and sign the proof; return the proof's SHA-256."""
    actual = hash_private(config["ciphertextFile"], config["ciphertextBytes"], CIPHERTEXT_LIMIT)
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
            hash_private(config["ciphertextFile"], config["ciphertextBytes"], CIPHERTEXT_LIMIT)
            == actual,
            "CIPHERTEXT_CHANGED",
        )
        body = canonical(proof_fields(config, length, digest, dict(verified)))
        write_new(config["outputProofFile"], body)
        detached = sign(config["verificationSigningKeyFile"], Path(config["outputProofFile"]))
        write_new(config["outputSignatureFile"], detached)
        return hashlib.sha256(body).hexdigest()


def main() -> int:
    try:
        require(len(sys.argv) == EXPECTED_ARGUMENTS, "ARGUMENTS_REFUSED")
        private_directory(str(Path(sys.argv[1]).parent))
        config = exact_json(read_private(sys.argv[1], CONFIG_LIMIT))
        validate(config)
        digest = execute(config)
    except VerificationFailureError as error:
        sys.stderr.write(
            (
                json.dumps(
                    {"status": "REFUSED", "reason": error.code, "realDataReady": False},
                    sort_keys=True,
                )
            )
            + "\n"
        )
        return REFUSED_EXIT
    sys.stdout.write(
        (
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
        + "\n"
    )
    return 0


def safe_error(
    _kind: type[BaseException], _error: BaseException, _traceback: TracebackType | None
) -> None:
    sys.stderr.write(
        (
            json.dumps(
                {"status": "REFUSED", "reason": "INSPECTION_UNAVAILABLE", "realDataReady": False},
                sort_keys=True,
            )
        )
        + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    raise SystemExit(main())
