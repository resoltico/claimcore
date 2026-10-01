"""Exact-ID synthetic override route into the fixed physical-copy verifier."""

import json
import sys
from pathlib import Path

from backup_types import JsonObject
from managed_copy_drill_common import REASON_PATTERN, VERIFIER, execute, private_json
from managed_copy_verification_io import (
    VerificationFailureError,
    exact_json,
    private_directory,
    read_private,
    require,
    uuid_text,
)
from verify_managed_copy import FIELDS

OVERRIDE_LIMIT_BYTES = 16384
COPY_REVISION = 2


def _check_override(override: JsonObject) -> None:
    require(set(override) == FIELDS | {"syntheticTestOverride"}, "SYNTHETIC_OVERRIDE_FIELDS")
    require(override["syntheticTestOverride"] is True, "SYNTHETIC_OVERRIDE_FLAG")
    require(
        override["format"] == "claimcore-managed-copy-verification-input-1",
        "SYNTHETIC_OVERRIDE_FORMAT",
    )
    require(override["kind"] in ("BASE", "WAL"), "SYNTHETIC_OVERRIDE_KIND")
    require(override["copyRevision"] == COPY_REVISION, "SYNTHETIC_OVERRIDE_REVISION")
    require(
        (
            override["kind"] == "WAL"
            and override["databaseName"] is None
            and override["databaseOwnerRole"] is None
        )
        or (
            override["kind"] == "BASE"
            and isinstance(override["databaseName"], str)
            and override["databaseName"].endswith("_test")
        ),
        "SYNTHETIC_OVERRIDE_DATABASE",
    )


def _refuse_from_verifier(stderr: str) -> None:
    try:
        reason = json.loads(stderr).get("reason")
    except (ValueError, TypeError, AttributeError):
        reason = None
    require(
        isinstance(reason, str) and REASON_PATTERN.fullmatch(reason) is not None,
        "REGISTERED_PROOF_REFUSED",
    )
    raise VerificationFailureError("REGISTERED_" + reason)


def _check_proof(status: JsonObject, config: JsonObject) -> None:
    proof = exact_json(read_private(config["outputProofFile"], OVERRIDE_LIMIT_BYTES))
    require(
        status.get("status") == "PHYSICAL_COPY_VERIFIED"
        and status.get("fullPairReady") is False
        and status.get("realDataReady") is False
        and proof["copyId"] == config["copyId"]
        and proof["copyEventId"] == config["copyEventId"]
        and proof["verificationEventId"] == config["verificationEventId"]
        and proof["copyRevision"] == COPY_REVISION
        and proof["locationCommitment"] == config["locationCommitment"]
        and proof["fullPairReady"] is False
        and proof["realDataReady"] is False,
        "REGISTERED_PROOF_MISMATCH",
    )


def registered_override(path: str) -> None:
    """Test-only exact-ID route; the packaged verifier still performs all physical checks."""
    private_directory(str(Path(path).parent))
    override = exact_json(read_private(path, OVERRIDE_LIMIT_BYTES))
    require(isinstance(override, dict), "SYNTHETIC_OVERRIDE_SHAPE")
    _check_override(override)
    config = {key: value for key, value in override.items() if key != "syntheticTestOverride"}
    uuid_text(config["verificationEventId"])
    private_directory(config["privateScratchRoot"])
    input_file = Path(config["privateScratchRoot"]) / (
        "registered-" + config["verificationEventId"] + "-input.json"
    )
    private_json(input_file, config)
    result = execute([str(VERIFIER), str(input_file)])
    if result.returncode != 0:
        _refuse_from_verifier(result.stderr)
    _check_proof(json.loads(result.stdout), config)
    sys.stdout.write("managed-copy-registered-proof=created" + "\n")
