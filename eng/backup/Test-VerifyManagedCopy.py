#!/usr/bin/env python3
"""Synthetic physical-copy verifier exercise over the isolated restore fixture."""

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from types import TracebackType
from typing import NoReturn

sys.dont_write_bytecode = True

from backup_types import JsonObject
from managed_copy_drill_common import (
    VERIFIER,
    execute,
    private_json,
    refusal_reason,
    run,
    stage_name,
)
from managed_copy_drill_fixture import ARGUMENT_COUNT, CopyFixture, DrillInputs
from managed_copy_drill_override import registered_override
from managed_copy_verification_io import VerificationFailureError

STAGE_PATTERN = re.compile(r"copy-proof-stage=[a-z-]{1,70}")
READINESS_FIELD = b'"realDataReady":false'


def fail_stage(label: str) -> NoReturn:
    """Stop with the stage-tagged failure the shell owner reports."""
    message = "copy-proof-stage=" + label
    raise RuntimeError(message)


def invoke_registered(
    fixture: CopyFixture,
    config: JsonObject,
    label: str,
    expected: int,
    expected_reason: str | None = None,
) -> subprocess.CompletedProcess[str]:
    override = {"syntheticTestOverride": True, **config}
    input_file = fixture.proof_root / (label + "-override.json")
    private_json(input_file, override)
    result = execute(
        [sys.executable, "-B", str(Path(__file__).resolve()), "--registered", str(input_file)]
    )
    if result.returncode != expected:
        reason = refusal_reason(result.stderr, "UNAVAILABLE")
        fail_stage(label + "-override-" + stage_name(reason))
    if expected != 0:
        refusal = json.loads(result.stderr)
        if (
            refusal.get("status") != "REFUSED"
            or refusal.get("reason") != expected_reason
            or Path(config["outputProofFile"]).exists()
            or Path(config["outputSignatureFile"]).exists()
        ):
            fail_stage(label + "-override-refusal")
    return result


def typed_refusal(stderr: str) -> JsonObject | None:
    try:
        refusal = json.loads(stderr)
    except ValueError:
        return None
    return refusal if isinstance(refusal, dict) else None


def _refusal_is_unsafe(refusal: JsonObject, config: JsonObject, label: str) -> bool:
    leaves_output = not label.endswith("-replay") and (
        Path(config["outputProofFile"]).exists() or Path(config["outputSignatureFile"]).exists()
    )
    return (
        refusal.get("status") != "REFUSED"
        or refusal.get("realDataReady") is not False
        or not isinstance(refusal.get("reason"), str)
        or leaves_output
    )


def invoke(
    fixture: CopyFixture, config: JsonObject, label: str, expected: int
) -> subprocess.CompletedProcess[str]:
    config_file = fixture.proof_root / (label + "-input.json")
    private_json(config_file, config)
    result = execute([str(VERIFIER), str(config_file)])
    if result.returncode != expected:
        fail_stage(label + "-" + stage_name(refusal_reason(result.stderr, "UNKNOWN")))
    if expected != 0:
        refusal = typed_refusal(result.stderr)
        if refusal is None:
            fail_stage(label + "-untyped")
        elif _refusal_is_unsafe(refusal, config, label):
            fail_stage(label + "-unsafe-refusal")
    return result


def openssl_verify(fixture: CopyFixture, document: Path, signature: Path, expected: int) -> None:
    run(
        [
            "openssl",
            "pkeyutl",
            "-verify",
            "-rawin",
            "-pubin",
            "-inkey",
            str(fixture.public_key),
            "-in",
            str(document),
            "-sigfile",
            str(signature),
        ],
        expected=expected,
    )


def check_proof_fields(config: JsonObject, object_file: Path) -> None:
    proof, signature = Path(config["outputProofFile"]), Path(config["outputSignatureFile"])
    text = proof.read_text(encoding="ascii")
    fields = json.loads(text)
    if (
        fields["purpose"] != "RESTORE_COPY_VERIFIER"
        or fields["fullPairReady"]
        or fields["realDataReady"]
        or fields["copyId"] != config["copyId"]
        or fields["nonce"] == config["witnessCutoffHash"]
        or len(signature.read_bytes()) != 64
        or str(object_file) in text
    ):
        msg = "managed-copy-verifier-test-proof"
        raise RuntimeError(msg)


def check_signature_and_tamper(fixture: CopyFixture, config: JsonObject, label: str) -> None:
    proof, signature = Path(config["outputProofFile"]), Path(config["outputSignatureFile"])
    openssl_verify(fixture, proof, signature, 0)
    source = proof.read_bytes()
    altered_body = source.replace(READINESS_FIELD, b'"realDataReady":true', 1)
    if altered_body == source:
        fail_stage("missing-readiness-field")
    tampered = fixture.proof_root / (label + "-tampered.json")
    descriptor = os.open(tampered, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(altered_body)
    openssl_verify(fixture, tampered, signature, 1)


def verify_positive(fixture: CopyFixture, kind: str, object_file: Path) -> None:
    label = kind.lower()
    config = fixture.candidate(kind, object_file, label)
    if kind == "BASE":
        result = invoke_registered(fixture, config, label, 0)
        if result.stdout.strip() != "managed-copy-registered-proof=created":
            fail_stage("base-override-status")
    elif json.loads(invoke(fixture, config, label, 0).stdout)["status"] != "PHYSICAL_COPY_VERIFIED":
        fail_stage("wal-status")
    check_proof_fields(config, object_file)
    check_signature_and_tamper(fixture, config, label)
    invoke(fixture, config, label + "-replay", 3)


def registered_negatives(fixture: CopyFixture) -> None:
    for label, change, reason in (
        ("not-test", {"syntheticTestOverride": False}, "SYNTHETIC_OVERRIDE_FLAG"),
        ("wrong-revision", {"copyRevision": 3}, "SYNTHETIC_OVERRIDE_REVISION"),
        ("production-db", {"databaseName": "claimcore_live"}, "SYNTHETIC_OVERRIDE_DATABASE"),
        ("path-event", {"verificationEventId": "../not-an-event"}, "IDENTITY_REFUSED"),
    ):
        invalid = {**fixture.candidate("BASE", fixture.base_object, label), **change}
        invoke_registered(fixture, invalid, label, 3, reason)


def alter_last_byte(source: Path, target: Path) -> None:
    shutil.copyfile(source, target)
    with target.open("r+b") as stream:
        stream.seek(-1, os.SEEK_END)
        last = stream.read(1)
        stream.seek(-1, os.SEEK_END)
        stream.write(bytes([last[0] ^ 1]))
    target.chmod(0o600)


def refused_object(fixture: CopyFixture, label: str, ciphertext_file: Path) -> None:
    bad = fixture.candidate("BASE", fixture.base_object, label)
    bad["ciphertextFile"] = str(ciphertext_file)
    invoke(fixture, bad, label, 3)


def object_negatives(fixture: CopyFixture) -> None:
    archive = fixture.inputs.scratch / "archive"
    altered = archive / "altered.tar.age"
    alter_last_byte(fixture.base_object, altered)
    refused_object(fixture, "altered", altered)
    linked = archive / "linked.tar.age"
    linked.symlink_to(fixture.base_object)
    refused_object(fixture, "linked", linked)
    hardlink = archive / "hardlinked.tar.age"
    os.link(fixture.base_object, hardlink)
    try:
        refused_object(fixture, "hardlinked", hardlink)
    finally:
        hardlink.unlink()
    refused_object(fixture, "escaped", fixture.inputs.scratch / "identity.age")
    bad = fixture.candidate("WAL", fixture.wal_object, "timeline")
    bad["timeline"] += 1
    invoke(fixture, bad, "timeline", 3)


def exercise(inputs: DrillInputs) -> None:
    fixture = CopyFixture.prepare(inputs)
    for kind, object_file in (("BASE", fixture.base_object), ("WAL", fixture.wal_object)):
        verify_positive(fixture, kind, object_file)
    registered_negatives(fixture)
    object_negatives(fixture)
    sys.stdout.write("managed-copy-physical-verifier=base-wal-and-negatives" + "\n")


def safe_error(
    _kind: type[BaseException], _error: BaseException, _traceback: TracebackType | None
) -> None:
    sys.stderr.write('{"status":"REFUSED","reason":"SYNTHETIC_OVERRIDE_REFUSED"}' + "\n")


def main(arguments: list[str]) -> None:
    if len(arguments) == 2 and arguments[0] == "--registered":
        try:
            registered_override(arguments[1])
        except VerificationFailureError as error:
            sys.stderr.write(json.dumps({"status": "REFUSED", "reason": error.code}) + "\n")
            raise SystemExit(3) from None
    elif len(arguments) == ARGUMENT_COUNT:
        try:
            exercise(DrillInputs.from_arguments(arguments))
        except RuntimeError as error:
            stage = str(error)
            if STAGE_PATTERN.fullmatch(stage):
                sys.stderr.write(stage + "\n")
                raise SystemExit(3) from None
            raise
    else:
        raise SystemExit(2)


if __name__ == "__main__":
    sys.excepthook = safe_error
    os.umask(0o077)
    main(sys.argv[1:])
