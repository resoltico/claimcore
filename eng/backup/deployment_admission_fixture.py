"""Synthetic qualification, role pins and signed probes for the deployment admission drill."""

import hashlib
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_test_support import ensure, keypair
from backup_types import JsonObject
from deployment_common import sign, utc, verify
from deployment_fenced_objects import final_objects_digest
from deployment_trust import ROLES, ProbeExpectation

MIB = 1024 * 1024
WAL_SEGMENT_BYTES = 16 * MIB
ENCRYPTED_OVERHEAD_BYTES = 32
PROBE_VALIDITY_SECONDS = 60
DATABASE_ROLES = ("primary", "witness")
OBJECT_ROLES = ("archive", "checkpoint")
AVAILABILITY_KIND = {
    "primary": "database-read",
    "witness": "database-read",
    "archive": "retained-copy",
    "checkpoint": "retained-copy",
    "key": "key-possession",
}
SAME_MACHINE = hashlib.sha256(b"same physical Mac").hexdigest()


def final_wal_objects() -> list[JsonObject]:
    """Build the two synthetic final-WAL object descriptions."""
    return [
        {
            "objectId": str(uuid.uuid4()),
            "cluster": cluster,
            "relativePath": f"fenced-tail/{cluster.lower()}/000000010000000000000001.age",
            "ciphertextSha256": f"{index:064x}",
            "ciphertextBytes": WAL_SEGMENT_BYTES + ENCRYPTED_OVERHEAD_BYTES,
            "walSegment": "000000010000000000000001",
            "walSegmentBytes": WAL_SEGMENT_BYTES,
        }
        for index, cluster in enumerate(("PRIMARY", "WITNESS"), 1)
    ]


def qualification() -> JsonObject:
    """Build a synthetic restore qualification with its final-WAL digest."""
    objects = final_wal_objects()
    return {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "primarySystemId": "1111111111111111111",
        "primaryTimeline": 1,
        "witnessSystemId": "2222222222222222222",
        "witnessTimeline": 1,
        "witnessCutoff": 1,
        "witnessCutoffHash": "a" * 64,
        "custodyObjects": {
            role: {
                "objectId": str(uuid.uuid4()),
                "sha256": hashlib.sha256(role.encode()).hexdigest(),
                "bytes": len(role),
            }
            for role in OBJECT_ROLES
        },
        "custodyKeyId": str(uuid.uuid4()),
        "custodyPublicKeySha256": "f" * 64,
        "finalWalObjects": objects,
        "finalWalObjectSha256": final_objects_digest({"walObjects": objects}),
    }


def challenge(qualified: JsonObject) -> ProbeExpectation:
    """Build the probe challenge for `qualified`."""
    return ProbeExpectation("a" * 64, "b" * 64, "c" * 64, qualified)


def _role_fields(role: str, pinned: JsonObject, qualified: JsonObject) -> JsonObject:
    if role in DATABASE_ROLES:
        fields: JsonObject = {
            "postgresSystemId": pinned["postgresSystemId"],
            "timeline": pinned["timeline"],
        }
        if role == "witness":
            fields["witnessTipSequence"] = qualified["witnessCutoff"]
            fields["witnessTipHash"] = qualified["witnessCutoffHash"]
        return fields
    if role in OBJECT_ROLES:
        fields = {
            "objectId": pinned["objectId"],
            "objectSha256": pinned["objectSha256"],
            "objectBytes": pinned["objectBytes"],
        }
        if role == "archive":
            fields["finalWalObjects"] = qualified["finalWalObjects"]
            fields["finalWalObjectCount"] = len(qualified["finalWalObjects"])
            fields["finalWalObjectSha256"] = qualified["finalWalObjectSha256"]
        return fields
    return {
        "custodyKeyId": pinned["custodyKeyId"],
        "custodyPublicKeySha256": pinned["custodyPublicKeySha256"],
        "softwareKeyExportable": True,
    }


def report(role: str, pinned: JsonObject, expect: ProbeExpectation) -> JsonObject:
    """Build the probe report `role` would sign for `pinned` against the challenge."""
    qualified = expect.qualification
    now = datetime.now(UTC)
    return {
        "format": "claimcore-deployment-probe-1",
        "role": role,
        "nonce": expect.nonce,
        "qualificationSha256": expect.qualification_sha,
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(seconds=PROBE_VALIDITY_SECONDS)),
        "machineHash": pinned["machineHash"],
        "storageHash": pinned["storageHash"],
        "adminActorId": pinned["adminActorId"],
        "hostKeyId": pinned["hostKeyId"],
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
        "containerized": False,
        "availabilityKind": AVAILABILITY_KIND[role],
        "available": True,
        "keyChallengeProofSha256": expect.key_proof if role == "key" else None,
        **_role_fields(role, pinned, qualified),
    }


def _role_pins(role: str, qualified: JsonObject) -> JsonObject:
    if role in DATABASE_ROLES:
        return {
            "postgresSystemId": qualified[role + "SystemId"],
            "timeline": qualified[role + "Timeline"],
        }
    if role in OBJECT_ROLES:
        item = qualified["custodyObjects"][role]
        return {
            "objectId": item["objectId"],
            "objectSha256": item["sha256"],
            "objectBytes": item["bytes"],
        }
    return {
        "custodyKeyId": qualified["custodyKeyId"],
        "custodyPublicKeySha256": qualified["custodyPublicKeySha256"],
    }


def pins(role: str, qualified: JsonObject, public: Path) -> JsonObject:
    """Owner-pinned facts for `role`; every role shares one synthetic machine."""
    return {
        "probePublicKey": str(public),
        "machineHash": SAME_MACHINE,
        "storageHash": hashlib.sha256(("storage " + role).encode()).hexdigest(),
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        **_role_pins(role, qualified),
    }


@dataclass
class Deployment:
    """A five-role deployment configuration with signed probes from one machine."""

    config: JsonObject
    qualified: JsonObject
    expect: ProbeExpectation
    envelopes: dict[str, JsonObject]
    keys: dict[str, Path]


def build_deployment(root: Path) -> Deployment:
    """Sign one probe per role for a deployment that shares one machine."""
    qualified = qualification()
    expect = challenge(qualified)
    roles: JsonObject = {}
    envelopes: dict[str, JsonObject] = {}
    keys: dict[str, Path] = {}
    for role in ROLES:
        private, public = keypair(root, role)
        keys[role] = private
        roles[role] = pins(role, qualified, public)
        envelopes[role] = sign(report(role, roles[role], expect), private)
        ensure(verify(envelopes[role], public)[0]["role"] == role, "probe-role-roundtrip")
    config: JsonObject = {
        "format": "claimcore-deployment-1",
        "mode": "production",
        "roles": roles,
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
    }
    return Deployment(config, qualified, expect, envelopes, keys)
