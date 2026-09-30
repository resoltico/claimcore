#!/usr/bin/env python3
"""Owner-private known-location inventory and signed inspection; never certifies erasure."""

import argparse
import hashlib
import json
import os
import stat
import sys
from datetime import UTC, datetime, timedelta
from pathlib import Path
from types import TracebackType

from backup_types import Json, JsonObject

sys.dont_write_bytecode = True
from deployment_common import (
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
    private_path,
    require,
    sign,
    utc,
    verify,
)

MAX_COPY_BYTES = 1 << 40
MAX_ENTRIES = 10000
MAX_CUSTODIAN_CHARS = 256
HASH_BLOCK = 1024 * 1024
REGISTRY_MINUTES = 10
INSPECTION_MINUTES = 5
ENTRY_FIELDS = {
    "copyId",
    "producerKind",
    "custodianId",
    "location",
    "kind",
    "sourceCaseId",
    "ciphertextSha256",
    "ciphertextBytes",
}
REGISTRY_FIELDS = {
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
}
COPY_KINDS = {
    "BASE",
    "WAL",
    "SNAPSHOT",
    "REPLICA",
    "WITNESS_PAYLOAD",
    "EXPORT",
    "ENCRYPTION_KEY_COPY",
}


def _require_uuid(value: Json) -> None:
    require(isinstance(value, str) and is_uuid(value), "copy-inventory-identity")


def _require_digest(value: Json) -> None:
    require(is_sha256(value), "copy-inventory-digest")


def _check_producer(entry: JsonObject) -> None:
    require(
        entry["producerKind"] in {"OWNER_ATTESTED", "PRODUCT_EXPORT"}, "copy-inventory-producer"
    )
    if entry["producerKind"] == "OWNER_ATTESTED":
        require(
            isinstance(entry["custodianId"], str)
            and 1 <= len(entry["custodianId"]) <= MAX_CUSTODIAN_CHARS,
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


def _entry(entry: JsonObject) -> None:
    require(set(entry) == ENTRY_FIELDS, "copy-inventory-entry")
    _require_uuid(entry["copyId"])
    _check_producer(entry)
    require(isinstance(entry["kind"], str) and entry["kind"] in COPY_KINDS, "copy-inventory-kind")
    if entry["sourceCaseId"] is not None:
        _require_uuid(entry["sourceCaseId"])
    _require_digest(entry["ciphertextSha256"])
    require(
        type(entry["ciphertextBytes"]) is int and 0 < entry["ciphertextBytes"] <= MAX_COPY_BYTES,
        "copy-inventory-size",
    )


def _check_header(body: JsonObject) -> None:
    require(set(body) == REGISTRY_FIELDS, "copy-inventory-format")
    require(body["format"] == "claimcore-copy-location-inventory-1", "copy-inventory-format")
    for name in ("installationId", "lineageId", "signingKeyId"):
        _require_uuid(body[name])
    require(
        type(body["epoch"]) is int
        and body["epoch"] > 0
        and type(body["witnessCutoffSequence"]) is int
        and body["witnessCutoffSequence"] >= 0,
        "copy-inventory-cutoff",
    )
    _require_digest(body["witnessCutoffHash"])
    require(
        isinstance(body["entries"], list) and len(body["entries"]) <= MAX_ENTRIES,
        "copy-inventory-bound",
    )
    require(
        isinstance(body["knownUnmanaged"], list) and len(body["knownUnmanaged"]) <= MAX_ENTRIES,
        "copy-inventory-unmanaged",
    )


def _registry(body: JsonObject) -> None:
    _check_header(body)
    ids: set[str] = set()
    for entry in body["entries"]:
        _entry(entry)
        require(entry["copyId"] not in ids, "copy-inventory-duplicate")
        ids.add(entry["copyId"])
    for unmanaged_id in body["knownUnmanaged"]:
        _require_uuid(unmanaged_id)
        require(unmanaged_id not in ids, "copy-inventory-duplicate")
        ids.add(unmanaged_id)
    issued = datetime.fromisoformat(body["issuedAt"])
    expires = datetime.fromisoformat(body["expiresAt"])
    now = datetime.now(UTC)
    require(
        issued <= now < expires and expires - issued <= timedelta(minutes=REGISTRY_MINUTES),
        "copy-inventory-expired",
    )


def _observation(entry: JsonObject, maximum: int) -> JsonObject:
    absent = {"copyId": entry["copyId"], "sha256": None, "bytes": None}
    if entry["location"] is None:
        return {**absent, "status": "UNKNOWN"}
    location = Path(entry["location"])
    private_path(location.parent, directory=True)
    try:
        mode = os.lstat(location).st_mode
    except FileNotFoundError:
        return {**absent, "status": "ABSENT"}
    require(stat.S_ISREG(mode), "copy-location-unsafe")
    source = private_path(location)
    require(source.stat().st_size <= maximum, "copy-inspection-bound")
    total = 0
    digest = hashlib.sha256()
    with source.open("rb") as stream:
        for block in iter(lambda: stream.read(HASH_BLOCK), b""):
            total += len(block)
            require(total <= maximum, "copy-inspection-bound")
            digest.update(block)
    return {
        "copyId": entry["copyId"],
        "status": "PRESENT",
        "sha256": digest.hexdigest(),
        "bytes": total,
    }


def inspect(
    registry_envelope: JsonObject,
    public_key: str | Path,
    verifier_private: str | Path,
    verifier_key_id: str,
    maximum: int,
) -> JsonObject:
    """Observe every registered location and return the signed inspection report."""
    registry, registry_sha = verify(registry_envelope, public_key)
    _registry(registry)
    observations = [_observation(entry, maximum) for entry in registry["entries"]]
    now = datetime.now(UTC)
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
        "expiresAt": utc(now + timedelta(minutes=INSPECTION_MINUTES)),
        "observations": observations,
    }
    return sign(report, verifier_private)


def _write_new(path: str | Path, content: JsonObject) -> None:
    parent = private_path(Path(path).parent, directory=True)
    target = parent / Path(path).name
    with target.open("xb") as stream:
        stream.write(canonical(content))
        stream.flush()
        os.fsync(stream.fileno())
    target.chmod(0o600)


def main() -> None:
    """Inspect the registered locations and write the signed report."""
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", required=True)
    parser.add_argument("--registry-public-key", required=True)
    parser.add_argument("--verifier-private-key", required=True)
    parser.add_argument("--verifier-key-id", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--maximum-copy-bytes", type=int, required=True)
    args = parser.parse_args()
    require(0 < args.maximum_copy_bytes <= MAX_COPY_BYTES, "copy-inspection-bound")
    _require_uuid(args.verifier_key_id)
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


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    category = (
        error.args[0] if isinstance(error, DeploymentRefusalError) else "copy-inspection-refused"
    )
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": category, "realDataReady": False}) + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()
