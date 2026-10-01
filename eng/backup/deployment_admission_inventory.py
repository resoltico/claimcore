"""Copy-location inventory negatives for deployment admission."""

import hashlib
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from functools import partial
from pathlib import Path

from backup_test_support import ensure, keypair, refuses
from backup_types import JsonObject
from deployment_common import sign, utc
from location_inspection import inspect as inspect_locations

INSPECTION_BYTE_LIMIT = 1024
INVENTORY_ENTRY_LIMIT = 10000
EXPIRED = "2000-01-01T00:00:00Z"


@dataclass
class Registry:
    """A signed copy-location inventory and the keys that sign and inspect it."""

    document: JsonObject
    entry: JsonObject
    signer: Path
    public: Path
    inspector: Path

    def inspect(self, document: JsonObject | None = None) -> JsonObject:
        """Sign `document` (default: the registry) and inspect its locations."""
        report: JsonObject = inspect_locations(
            sign(document or self.document, self.signer),
            self.public,
            self.inspector,
            str(uuid.uuid4()),
            INSPECTION_BYTE_LIMIT,
        )
        return report

    def changed(self, **fields: object) -> JsonObject:
        """Return the registry document with `fields` replaced."""
        return {**self.document, **fields}

    def observed(self, document: JsonObject | None = None) -> JsonObject:
        """Return the first observation of inspecting `document`."""
        observation: JsonObject = self.inspect(document)["report"]["observations"][0]
        return observation


def _registry(root: Path, copy: Path) -> Registry:
    signer, public = keypair(root, "registry-signer")
    inspector, _ = keypair(root, "inspection-signer")
    now = datetime.now(UTC)
    entry: JsonObject = {
        "copyId": str(uuid.uuid4()),
        "producerKind": "OWNER_ATTESTED",
        "custodianId": "synthetic-owner",
        "location": str(copy),
        "kind": "BASE",
        "sourceCaseId": None,
        "ciphertextSha256": hashlib.sha256(copy.read_bytes()).hexdigest(),
        "ciphertextBytes": copy.stat().st_size,
    }
    document: JsonObject = {
        "format": "claimcore-copy-location-inventory-1",
        "signingKeyId": str(uuid.uuid4()),
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "witnessCutoffSequence": 1,
        "witnessCutoffHash": "a" * 64,
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(minutes=5)),
        "entries": [entry],
        "knownUnmanaged": [],
    }
    return Registry(document, entry, signer, public, inspector)


def _observation_outcomes(registry: Registry, copy: Path) -> None:
    report = registry.inspect()["report"]
    ensure(report["observations"][0]["status"] == "PRESENT", "copy-present")
    ensure(
        report["observations"][0]["sha256"] == registry.entry["ciphertextSha256"],
        "copy-digest",
    )
    copy.unlink()
    missing = registry.inspect()["report"]
    ensure(missing["observations"][0]["status"] == "ABSENT", "copy-absent")
    ensure(missing.get("realDataReady") is None, "inspection-is-not-readiness")
    liability = registry.inspect(registry.changed(knownUnmanaged=[str(uuid.uuid4())]))["report"]
    ensure(liability["registrySha256"] != report["registrySha256"], "unmanaged-changes-registry")
    product = {
        **registry.entry,
        "copyId": str(uuid.uuid4()),
        "producerKind": "PRODUCT_EXPORT",
        "kind": "EXPORT",
        "custodianId": None,
        "location": None,
    }
    unknown = registry.observed(registry.changed(entries=[product]))
    ensure(unknown["status"] == "UNKNOWN", "product-export-unknown")


def _inventory_refusals(registry: Registry) -> None:
    entry = registry.entry
    for document, category in (
        (registry.changed(entries=[entry, entry]), "copy-inventory-duplicate"),
        (registry.changed(entries=[entry] * (INVENTORY_ENTRY_LIMIT + 1)), "copy-inventory-bound"),
        (registry.changed(expiresAt=EXPIRED), "copy-inventory-expired"),
    ):
        refuses(partial(registry.inspect, document), category)


def location_inventory_negative(root: Path) -> None:
    """Report what exists without asserting readiness, and refuse bad inventories."""
    copy_root = root / "private-copy"
    copy_root.mkdir(mode=0o700)
    copy = copy_root / "known-copy.age"
    copy.write_bytes(b"synthetic ciphertext")
    copy.chmod(0o600)
    registry = _registry(root, copy)
    _observation_outcomes(registry, copy)
    _inventory_refusals(registry)
