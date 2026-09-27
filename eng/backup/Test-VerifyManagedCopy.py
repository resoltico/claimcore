#!/usr/bin/env python3
"""Synthetic physical-copy verifier exercise over the isolated restore fixture."""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from pathlib import Path

sys.dont_write_bytecode = True

from managed_copy_verification_io import (
    VerificationFailure,
    exact_json,
    private_directory,
    read_private,
    require,
    uuid_text,
)
from verify_managed_copy import FIELDS


def run(args, expected=0):
    result = subprocess.run(
        args, capture_output=True, text=True, timeout=240, check=False
    )
    if result.returncode != expected:
        raise RuntimeError("managed-copy-verifier-test-refused")
    return result.stdout


def private_json(path, value):
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "w", encoding="ascii") as stream:
        json.dump(value, stream, sort_keys=True, separators=(",", ":"))
        stream.write("\n")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def registered_override(path):
    """Test-only exact-ID route; the packaged verifier still performs all physical checks."""
    private_directory(str(Path(path).parent))
    override = exact_json(read_private(path, 16384))
    require(isinstance(override, dict), "SYNTHETIC_OVERRIDE_SHAPE")
    require(
        set(override) == FIELDS | {"syntheticTestOverride"}, "SYNTHETIC_OVERRIDE_FIELDS"
    )
    require(override["syntheticTestOverride"] is True, "SYNTHETIC_OVERRIDE_FLAG")
    require(
        override["format"] == "claimcore-managed-copy-verification-input-1",
        "SYNTHETIC_OVERRIDE_FORMAT",
    )
    require(override["kind"] in ("BASE", "WAL"), "SYNTHETIC_OVERRIDE_KIND")
    require(override["copyRevision"] == 2, "SYNTHETIC_OVERRIDE_REVISION")
    require(
        override["kind"] == "WAL"
        and override["databaseName"] is None
        and override["databaseOwnerRole"] is None
        or override["kind"] == "BASE"
        and isinstance(override["databaseName"], str)
        and override["databaseName"].endswith("_test"),
        "SYNTHETIC_OVERRIDE_DATABASE",
    )
    config = dict(override)
    del config["syntheticTestOverride"]
    uuid_text(config["verificationEventId"])
    private_directory(config["privateScratchRoot"])
    input_file = Path(config["privateScratchRoot"]) / (
        "registered-" + config["verificationEventId"] + "-input.json"
    )
    private_json(input_file, config)
    verifier = Path(__file__).resolve().parent / "Verify-ManagedCopy.sh"
    result = subprocess.run(
        [str(verifier), str(input_file)],
        capture_output=True,
        text=True,
        timeout=240,
        check=False,
    )
    if result.returncode != 0:
        try:
            refusal = json.loads(result.stderr)
            reason = refusal.get("reason")
        except (ValueError, TypeError):
            reason = None
        require(
            isinstance(reason, str)
            and re.fullmatch(r"[A-Z_]{1,70}", reason) is not None,
            "REGISTERED_PROOF_REFUSED",
        )
        raise VerificationFailure("REGISTERED_" + reason)
    status = json.loads(result.stdout)
    proof = exact_json(read_private(config["outputProofFile"], 16384))
    require(
        status.get("status") == "PHYSICAL_COPY_VERIFIED"
        and status.get("fullPairReady") is False
        and status.get("realDataReady") is False
        and proof["copyId"] == config["copyId"]
        and proof["copyEventId"] == config["copyEventId"]
        and proof["verificationEventId"] == config["verificationEventId"]
        and proof["copyRevision"] == 2
        and proof["locationCommitment"] == config["locationCommitment"]
        and proof["fullPairReady"] is False
        and proof["realDataReady"] is False,
        "REGISTERED_PROOF_MISMATCH",
    )
    print("managed-copy-registered-proof=created")


def exercise(
    scratch,
    installation,
    lineage,
    epoch,
    sequence,
    cutoff_hash,
    owner_role,
    database_name,
):
    root = Path(__file__).resolve().parent
    manifest_file = scratch / "primary" / "backup_manifest"
    manifest = json.loads(manifest_file.read_text(encoding="ascii"))
    (wal_range,) = manifest["WAL-Ranges"]
    timeline = int(wal_range["Timeline"])
    system = str(manifest["System-Identifier"])
    segments = sorted(
        path
        for path in (scratch / "primary" / "pg_wal").iterdir()
        if re.fullmatch(r"[0-9A-F]{24}", path.name)
    )
    if not segments:
        raise RuntimeError("managed-copy-verifier-test-no-wal")
    segment = segments[0]
    proof_root = scratch / "copy-proofs"
    proof_root.mkdir(mode=0o700)
    signing_key = proof_root / "verifier.key"
    public_key = proof_root / "verifier.pub"
    run(["openssl", "genpkey", "-algorithm", "Ed25519", "-out", str(signing_key)])
    run(
        ["openssl", "pkey", "-in", str(signing_key), "-pubout", "-out", str(public_key)]
    )
    os.chmod(signing_key, 0o600)
    os.chmod(public_key, 0o600)

    def candidate(kind, object_file, label):
        base = kind == "BASE"
        return {
            "format": "claimcore-managed-copy-verification-input-1",
            "verificationEventId": str(uuid.uuid4()),
            "copyId": str(uuid.uuid4()),
            "copyEventId": str(uuid.uuid4()),
            "copyRevision": 2,
            "archiveObjectId": str(uuid.uuid4()),
            "installationId": installation,
            "lineageId": lineage,
            "witnessEpoch": epoch,
            "witnessCutoffSequence": sequence,
            "witnessCutoffHash": cutoff_hash,
            "cluster": "PRIMARY",
            "kind": kind,
            "postgresSystemId": system,
            "timeline": timeline,
            "walSegmentBytes": segment.stat().st_size,
            "backupManifestSha256": digest(manifest_file) if base else None,
            "walStartLsn": wal_range["Start-LSN"] if base else None,
            "walEndLsn": wal_range["End-LSN"] if base else None,
            "walSegment": None if base else segment.name,
            "locationCommitment": os.urandom(32).hex(),
            "ciphertextSha256": digest(object_file),
            "ciphertextBytes": object_file.stat().st_size,
            "ciphertextFile": str(object_file),
            "archiveRoot": str(scratch / "archive"),
            "ageIdentityFile": str(scratch / "identity.age"),
            "verificationSigningKeyFile": str(signing_key),
            "verifierSigningKeyId": str(uuid.uuid4()),
            "verifierHolderActorId": str(uuid.uuid4()),
            "outputProofFile": str(proof_root / (label + ".json")),
            "outputSignatureFile": str(proof_root / (label + ".sig")),
            "privateScratchRoot": str(proof_root),
            "maximumPlaintextBytes": 1073741824,
            "maximumTarEntries": 100000,
            "databaseOwnerRole": owner_role if base else None,
            "databaseName": database_name if base else None,
        }

    verifier = root / "Verify-ManagedCopy.sh"

    def invoke_registered(config, label, expected, expected_reason=None):
        override = dict(config)
        override.setdefault("syntheticTestOverride", True)
        input_file = proof_root / (label + "-override.json")
        private_json(input_file, override)
        result = subprocess.run(
            [
                sys.executable,
                "-B",
                str(Path(__file__).resolve()),
                "--registered",
                str(input_file),
            ],
            capture_output=True,
            text=True,
            timeout=240,
            check=False,
        )
        if result.returncode != expected:
            try:
                reason = json.loads(result.stderr).get("reason", "UNAVAILABLE")
            except (ValueError, TypeError):
                reason = "UNAVAILABLE"
            safe = re.sub(r"[^a-z-]", "", str(reason).lower().replace("_", "-"))
            raise RuntimeError("copy-proof-stage=" + label + "-override-" + safe)
        if expected != 0:
            refusal = json.loads(result.stderr)
            if (
                refusal.get("status") != "REFUSED"
                or refusal.get("reason") != expected_reason
                or Path(config["outputProofFile"]).exists()
                or Path(config["outputSignatureFile"]).exists()
            ):
                raise RuntimeError("copy-proof-stage=" + label + "-override-refusal")
        return result

    def invoke(config, label, expected):
        config_file = proof_root / (label + "-input.json")
        private_json(config_file, config)
        result = subprocess.run(
            [str(verifier), str(config_file)],
            capture_output=True,
            text=True,
            timeout=240,
            check=False,
        )
        if result.returncode != expected:
            try:
                reason = json.loads(result.stderr).get("reason", "UNKNOWN")
            except (ValueError, TypeError):
                reason = "UNKNOWN"
            safe = re.sub(r"[^a-z-]", "", str(reason).lower().replace("_", "-"))
            raise RuntimeError("copy-proof-stage=" + label + "-" + safe)
        if expected != 0:
            try:
                refusal = json.loads(result.stderr)
            except ValueError:
                raise RuntimeError("copy-proof-stage=" + label + "-untyped") from None
            if (
                refusal.get("status") != "REFUSED"
                or refusal.get("realDataReady") is not False
                or not isinstance(refusal.get("reason"), str)
                or (
                    not label.endswith("-replay")
                    and (
                        Path(config["outputProofFile"]).exists()
                        or Path(config["outputSignatureFile"]).exists()
                    )
                )
            ):
                raise RuntimeError("copy-proof-stage=" + label + "-unsafe-refusal")
        return result

    for kind, object_file in (
        ("BASE", scratch / "archive" / "primary.tar.age"),
        ("WAL", scratch / "archive" / "primary-wal" / (segment.name + ".age")),
    ):
        label = kind.lower()
        config = candidate(kind, object_file, label)
        if kind == "BASE":
            result = invoke_registered(config, label, 0)
            if result.stdout.strip() != "managed-copy-registered-proof=created":
                raise RuntimeError("copy-proof-stage=base-override-status")
        else:
            outcome = invoke(config, label, 0)
            result = json.loads(outcome.stdout)
            if result["status"] != "PHYSICAL_COPY_VERIFIED":
                raise RuntimeError("copy-proof-stage=wal-status")
        proof = Path(config["outputProofFile"])
        signature = Path(config["outputSignatureFile"])
        fields = json.loads(proof.read_text(encoding="ascii"))
        if (
            fields["purpose"] != "RESTORE_COPY_VERIFIER"
            or fields["fullPairReady"]
            or fields["realDataReady"]
            or fields["copyId"] != config["copyId"]
            or fields["nonce"] == config["witnessCutoffHash"]
            or len(signature.read_bytes()) != 64
            or str(object_file) in proof.read_text(encoding="ascii")
        ):
            raise RuntimeError("managed-copy-verifier-test-proof")
        run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-rawin",
                "-pubin",
                "-inkey",
                str(public_key),
                "-in",
                str(proof),
                "-sigfile",
                str(signature),
            ]
        )
        tampered = proof_root / (label + "-tampered.json")
        source = proof.read_bytes()
        altered_body = source.replace(
            b'"realDataReady":false', b'"realDataReady":true', 1
        )
        if altered_body == source:
            raise RuntimeError("copy-proof-stage=missing-readiness-field")
        descriptor = os.open(tampered, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(altered_body)
        run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-rawin",
                "-pubin",
                "-inkey",
                str(public_key),
                "-in",
                str(tampered),
                "-sigfile",
                str(signature),
            ],
            expected=1,
        )
        invoke(config, label + "-replay", 3)

    for label, change, reason in (
        ("not-test", {"syntheticTestOverride": False}, "SYNTHETIC_OVERRIDE_FLAG"),
        ("wrong-revision", {"copyRevision": 3}, "SYNTHETIC_OVERRIDE_REVISION"),
        (
            "production-db",
            {"databaseName": "claimcore_live"},
            "SYNTHETIC_OVERRIDE_DATABASE",
        ),
        ("path-event", {"verificationEventId": "../not-an-event"}, "IDENTITY_REFUSED"),
    ):
        invalid = candidate("BASE", scratch / "archive" / "primary.tar.age", label)
        invalid.update(change)
        invoke_registered(invalid, label, 3, reason)

    original = scratch / "archive" / "primary.tar.age"
    altered = scratch / "archive" / "altered.tar.age"
    shutil.copyfile(original, altered)
    with altered.open("r+b") as stream:
        stream.seek(-1, os.SEEK_END)
        last = stream.read(1)
        stream.seek(-1, os.SEEK_END)
        stream.write(bytes([last[0] ^ 1]))
    os.chmod(altered, 0o600)
    bad = candidate("BASE", original, "altered")
    bad["ciphertextFile"] = str(altered)
    invoke(bad, "altered", 3)
    linked = scratch / "archive" / "linked.tar.age"
    linked.symlink_to(original)
    bad = candidate("BASE", original, "linked")
    bad["ciphertextFile"] = str(linked)
    invoke(bad, "linked", 3)
    hardlink = scratch / "archive" / "hardlinked.tar.age"
    os.link(original, hardlink)
    try:
        bad = candidate("BASE", original, "hardlinked")
        bad["ciphertextFile"] = str(hardlink)
        invoke(bad, "hardlinked", 3)
    finally:
        hardlink.unlink()
    bad = candidate("BASE", original, "escaped")
    bad["ciphertextFile"] = str(scratch / "identity.age")
    invoke(bad, "escaped", 3)
    bad = candidate(
        "WAL", scratch / "archive" / "primary-wal" / (segment.name + ".age"), "timeline"
    )
    bad["timeline"] += 1
    invoke(bad, "timeline", 3)
    print("managed-copy-physical-verifier=base-wal-and-negatives")


if __name__ == "__main__":

    def safe_error(_kind, _error, _traceback):
        print(
            '{"status":"REFUSED","reason":"SYNTHETIC_OVERRIDE_REFUSED"}',
            file=sys.stderr,
        )

    sys.excepthook = safe_error
    os.umask(0o077)
    if len(sys.argv) == 3 and sys.argv[1] == "--registered":
        try:
            registered_override(sys.argv[2])
        except VerificationFailure as error:
            print(
                json.dumps({"status": "REFUSED", "reason": error.code}), file=sys.stderr
            )
            raise SystemExit(3) from None
    elif len(sys.argv) == 9:
        try:
            exercise(
                Path(sys.argv[1]),
                sys.argv[2],
                sys.argv[3],
                int(sys.argv[4]),
                int(sys.argv[5]),
                sys.argv[6],
                sys.argv[7],
                sys.argv[8],
            )
        except RuntimeError as error:
            stage = str(error)
            if re.fullmatch(r"copy-proof-stage=[a-z-]{1,70}", stage):
                print(stage, file=sys.stderr)
                raise SystemExit(3) from None
            raise
    else:
        raise SystemExit(2)
