#!/usr/bin/env python3
"""Owner-private known-location inventory and separate signed inspection; never certifies erasure."""

import argparse
import hashlib
import json
import os
import re
import stat
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from deployment_common import (
    DeploymentRefusal,
    canonical,
    private_path,
    require,
    sign,
    utc,
    verify,
)


def _uuid(value):
    import uuid

    require(isinstance(value, str), "copy-inventory-identity")
    parsed = uuid.UUID(value)
    require(parsed.int != 0 and str(parsed) == value, "copy-inventory-identity")


def _digest(value):
    require(
        isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value),
        "copy-inventory-digest",
    )


def _entry(entry):
    require(
        set(entry)
        == {
            "copyId",
            "producerKind",
            "custodianId",
            "location",
            "kind",
            "sourceCaseId",
            "ciphertextSha256",
            "ciphertextBytes",
        },
        "copy-inventory-entry",
    )
    _uuid(entry["copyId"])
    require(
        entry["producerKind"] in {"OWNER_ATTESTED", "PRODUCT_EXPORT"},
        "copy-inventory-producer",
    )
    if entry["producerKind"] == "OWNER_ATTESTED":
        require(
            isinstance(entry["custodianId"], str)
            and 1 <= len(entry["custodianId"]) <= 256,
            "copy-inventory-custodian",
        )
        require(
            isinstance(entry["location"], str)
            and entry["location"].startswith("/")
            and ".." not in Path(entry["location"]).parts,
            "copy-inventory-location",
        )
    else:
        require(
            entry["kind"] == "EXPORT"
            and entry["custodianId"] is None
            and entry["location"] is None,
            "copy-inventory-product-export",
        )
    require(
        isinstance(entry["kind"], str)
        and entry["kind"]
        in {
            "BASE",
            "WAL",
            "SNAPSHOT",
            "REPLICA",
            "WITNESS_PAYLOAD",
            "EXPORT",
            "ENCRYPTION_KEY_COPY",
        },
        "copy-inventory-kind",
    )
    if entry["sourceCaseId"] is not None:
        _uuid(entry["sourceCaseId"])
    _digest(entry["ciphertextSha256"])
    require(
        type(entry["ciphertextBytes"]) is int
        and 0 < entry["ciphertextBytes"] <= 1 << 40,
        "copy-inventory-size",
    )


def _registry(body):
    require(
        set(body)
        == {
            "format",
            "signingKeyId",
            "installationId",
            "lineageId",
            "epoch",
            "witnessCutoffSequence",
            "witnessCutoffHash",
            "issuedAt",
            "expiresAt",
            "entries",
            "knownUnmanaged",
        },
        "copy-inventory-format",
    )
    require(
        body["format"] == "claimcore-copy-location-inventory-1", "copy-inventory-format"
    )
    _uuid(body["installationId"])
    _uuid(body["lineageId"])
    _uuid(body["signingKeyId"])
    require(
        type(body["epoch"]) is int
        and body["epoch"] > 0
        and type(body["witnessCutoffSequence"]) is int
        and body["witnessCutoffSequence"] >= 0,
        "copy-inventory-cutoff",
    )
    _digest(body["witnessCutoffHash"])
    require(
        isinstance(body["entries"], list) and len(body["entries"]) <= 10000,
        "copy-inventory-bound",
    )
    require(
        isinstance(body["knownUnmanaged"], list)
        and len(body["knownUnmanaged"]) <= 10000,
        "copy-inventory-unmanaged",
    )
    ids = set()
    for entry in body["entries"]:
        _entry(entry)
        require(entry["copyId"] not in ids, "copy-inventory-duplicate")
        ids.add(entry["copyId"])
    for unmanaged_id in body["knownUnmanaged"]:
        _uuid(unmanaged_id)
        require(unmanaged_id not in ids, "copy-inventory-duplicate")
        ids.add(unmanaged_id)
    issued = datetime.fromisoformat(body["issuedAt"].replace("Z", "+00:00"))
    expires = datetime.fromisoformat(body["expiresAt"].replace("Z", "+00:00"))
    now = datetime.now(timezone.utc)
    require(
        issued <= now < expires and expires - issued <= timedelta(minutes=10),
        "copy-inventory-expired",
    )


def _observation(entry, maximum):
    if entry["location"] is None:
        return {
            "copyId": entry["copyId"],
            "status": "UNKNOWN",
            "sha256": None,
            "bytes": None,
        }
    location = Path(entry["location"])
    private_path(location.parent, directory=True)
    try:
        mode = os.lstat(location).st_mode
    except FileNotFoundError:
        return {
            "copyId": entry["copyId"],
            "status": "ABSENT",
            "sha256": None,
            "bytes": None,
        }
    require(stat.S_ISREG(mode), "copy-location-unsafe")
    source = private_path(location)
    require(source.stat().st_size <= maximum, "copy-inspection-bound")
    total = 0
    digest = hashlib.sha256()
    with source.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            total += len(block)
            require(total <= maximum, "copy-inspection-bound")
            digest.update(block)
    return {
        "copyId": entry["copyId"],
        "status": "PRESENT",
        "sha256": digest.hexdigest(),
        "bytes": total,
    }


def inspect(registry_envelope, public_key, verifier_private, verifier_key_id, maximum):
    registry, registry_sha = verify(registry_envelope, public_key)
    _registry(registry)
    observations = [_observation(entry, maximum) for entry in registry["entries"]]
    now = datetime.now(timezone.utc)
    report = {
        "format": "claimcore-copy-location-inspection-1",
        "signingKeyId": verifier_key_id,
        "registrySha256": registry_sha,
        "installationId": registry["installationId"],
        "lineageId": registry["lineageId"],
        "epoch": registry["epoch"],
        "witnessCutoffSequence": registry["witnessCutoffSequence"],
        "witnessCutoffHash": registry["witnessCutoffHash"],
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(minutes=5)),
        "observations": observations,
    }
    return sign(report, verifier_private)


def _write_new(path, content):
    parent = private_path(Path(path).parent, directory=True)
    target = parent / Path(path).name
    with target.open("xb") as stream:
        stream.write(canonical(content))
        stream.flush()
        os.fsync(stream.fileno())
    target.chmod(0o600)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", required=True)
    parser.add_argument("--registry-public-key", required=True)
    parser.add_argument("--verifier-private-key", required=True)
    parser.add_argument("--verifier-key-id", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--maximum-copy-bytes", type=int, required=True)
    args = parser.parse_args()
    require(0 < args.maximum_copy_bytes <= 1 << 40, "copy-inspection-bound")
    _uuid(args.verifier_key_id)
    envelope = json.loads(private_path(args.registry).read_bytes())
    result = inspect(
        envelope,
        args.registry_public_key,
        args.verifier_private_key,
        args.verifier_key_id,
        args.maximum_copy_bytes,
    )
    _write_new(args.output, result)
    sys.stdout.write('{"status":"inspection-signed","realDataReady":false}\n')


def safe_error(_kind, error, _traceback):
    category = (
        error.args[0]
        if isinstance(error, DeploymentRefusal)
        else "copy-inspection-refused"
    )
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": category, "realDataReady": False})
        + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()
