"""Synthetic six-host signed topology and observer fixture."""

import base64
import hashlib
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path

from backup_test_support import keypair
from backup_types import JsonObject
from deployment_aggregate_model import AVAILABILITY
from deployment_common import canonical, sign, utc
from deployment_fenced_objects import final_objects_digest
from deployment_topology import ROLES, verify_topology

WAL_SEGMENT_BYTES = 16 * 1024 * 1024
FENCE_FIELDS = (
    "oldEndpointId",
    "oldEndpointAddressSha256",
    "oldPrimaryRoleOid",
    "oldWitnessRoleOid",
    "oldPrimaryCredentialSha256",
    "oldWitnessCredentialSha256",
    "primarySessionSetSha256",
    "witnessSessionSetSha256",
)


@dataclass(frozen=True)
class AggregateFixture:
    """Everything a test needs to build and attack a synthetic aggregate."""

    root_public: Path
    aggregate_private: Path
    aggregate_public: Path
    observer_private: Path
    observer_public: Path
    config: JsonObject
    topology: JsonObject
    topology_sha: str
    envelopes: dict[str, JsonObject]
    role_keys: dict[str, Path]
    observation: JsonObject
    fenced: JsonObject
    nonce: str
    report_sha: str


def document(root: Path, name: str, value: JsonObject, key: Path) -> tuple[Path, Path]:
    """Write a canonical document and its detached signature beside `root`."""
    envelope = sign(value, key)
    raw, signed = root / (name + ".json"), root / (name + ".sig")
    raw.write_bytes(canonical(value))
    signed.write_bytes(base64.b64decode(envelope["signatureBase64"]))
    raw.chmod(0o600)
    signed.chmod(0o600)
    return raw, signed


def _tail(installation: str, lineage: str) -> JsonObject:
    return {
        "installationId": installation,
        "lineageId": lineage,
        "epoch": 1,
        "oldGeneration": 1,
        "newGeneration": 2,
        "w1Sequence": 11,
        "w1Hash": "e" * 64,
        "walObjects": [
            {
                "objectId": str(uuid.uuid4()),
                "cluster": cluster,
                "relativePath": f"fenced-tail/{cluster.lower()}/000000010000000000000001.age",
                "ciphertextSha256": f"{number:064x}",
                "ciphertextBytes": WAL_SEGMENT_BYTES + 32,
                "walSegment": "000000010000000000000001",
                "walSegmentBytes": WAL_SEGMENT_BYTES,
            }
            for number, cluster in enumerate(("PRIMARY", "WITNESS"), 1)
        ],
    }


def _role_pin(role: str, number: int, public: Path) -> JsonObject:
    return {
        "role": role,
        "probePublicKeySha256": hashlib.sha256(public.read_bytes()).hexdigest(),
        "machineHash": f"{number:064x}",
        "storageHash": f"{number + 10:064x}",
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        "sshHostKeySha256": f"{number + 20:064x}",
    }


def _probe_report(
    pin: JsonObject, nonce: str, tail_sha: str, now: datetime, fenced: JsonObject
) -> JsonObject:
    role = pin["role"]
    report = {
        "format": "claimcore-deployment-probe-1",
        "role": role,
        "nonce": nonce,
        "qualificationSha256": tail_sha,
        "availabilityKind": AVAILABILITY[role],
        "available": True,
        "containerized": False,
        "machineHash": pin["machineHash"],
        "storageHash": pin["storageHash"],
        "adminActorId": pin["adminActorId"],
        "hostKeyId": pin["hostKeyId"],
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(seconds=60)),
    }
    if role == "archive":
        tail = fenced["tail"]
        report.update(
            finalWalObjects=tail["walObjects"],
            finalWalObjectCount=len(tail["walObjects"]),
            finalWalObjectSha256=fenced["finalWalObjectSha256"],
        )
    return report


def _observer_pin(observer_public: Path) -> JsonObject:
    return {
        "role": "old-writer-fence",
        "oldEndpointId": str(uuid.uuid4()),
        "oldEndpointAddressSha256": "1" * 64,
        "oldPrimaryRoleOid": 101,
        "oldWitnessRoleOid": 102,
        "oldPrimaryCredentialSha256": "2" * 64,
        "oldWitnessCredentialSha256": "3" * 64,
        "primarySessionSetSha256": "4" * 64,
        "witnessSessionSetSha256": "5" * 64,
        "probePublicKeySha256": hashlib.sha256(observer_public.read_bytes()).hexdigest(),
        "machineHash": "6" * 64,
        "storageHash": "7" * 64,
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        "sshHostKeySha256": "8" * 64,
    }


def _observation(
    pin: JsonObject, fenced: JsonObject, nonce: str, report_sha: str, now: datetime
) -> JsonObject:
    tail = fenced["tail"]
    return {
        "format": "claimcore-old-writer-fence-observation-1",
        "role": "old-writer-fence",
        "nonce": nonce,
        "installationId": tail["installationId"],
        "lineageId": tail["lineageId"],
        "epoch": 1,
        "oldGeneration": 1,
        "newGeneration": 2,
        "w1Sequence": 11,
        "w1Hash": tail["w1Hash"],
        "reportSha256": report_sha,
        "fenceReportSha256": fenced["fenceSha256"],
        **fenced["fence"],
        "routeClosed": True,
        "primarySessionsZero": True,
        "witnessSessionsZero": True,
        "primaryCredentialDenied": True,
        "witnessCredentialDenied": True,
        "containerized": False,
        "machineHash": pin["machineHash"],
        "storageHash": pin["storageHash"],
        "adminActorId": pin["adminActorId"],
        "hostKeyId": pin["hostKeyId"],
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(seconds=60)),
    }


def _topology(
    tail: JsonObject,
    aggregate_public: Path,
    pins: list[JsonObject],
    observer: JsonObject,
    now: datetime,
) -> JsonObject:
    return {
        "format": "claimcore-deployment-topology-1",
        "topologyId": str(uuid.uuid4()),
        "installationId": tail["installationId"],
        "lineageId": tail["lineageId"],
        "epoch": 1,
        "writerGeneration": 2,
        "w1Sequence": 11,
        "w1Hash": tail["w1Hash"],
        "publicationManifestSha256": "9" * 64,
        "deploymentVerifierSigningKeyId": str(uuid.uuid4()),
        "deploymentVerifierHolderActorId": str(uuid.uuid4()),
        "deploymentVerifierPublicKeySha256": hashlib.sha256(
            aggregate_public.read_bytes()
        ).hexdigest(),
        "rolePins": pins,
        "fenceObserverPin": observer,
        "issuedAt": utc(now),
        "validUntil": utc(now + timedelta(hours=1)),
    }


def fixture(root: Path, now: datetime) -> AggregateFixture:
    """Build a synthetic root-signed topology with six observers and their evidence."""
    root_private, root_public = keypair(root, "publication-root")
    aggregate_private, aggregate_public = keypair(root, "aggregate")
    observer_private, observer_public = keypair(root, "observer")
    nonce, report_sha, fence_sha, tail_sha = (character * 64 for character in "abcd")
    tail = _tail(str(uuid.uuid4()), str(uuid.uuid4()))
    fenced: JsonObject = {
        "tail": tail,
        "fenceSha256": fence_sha,
        "tailSha256": tail_sha,
        "finalWalObjectSha256": final_objects_digest(tail),
    }
    config: JsonObject = {"roles": {}}
    pins, envelopes, role_keys = [], {}, {}
    for number, role in enumerate(ROLES, 1):
        key, public = keypair(root, role)
        role_keys[role] = public
        pin = _role_pin(role, number, public)
        pins.append(pin)
        config["roles"][role] = {**pin, "probePublicKey": str(public)}
        envelopes[role] = sign(_probe_report(pin, nonce, tail_sha, now, fenced), key)
    observer_pin = _observer_pin(observer_public)
    config["fenceObserver"] = {**observer_pin, "probePublicKey": str(observer_public)}
    fenced["fence"] = {name: observer_pin[name] for name in FENCE_FIELDS}
    topology = _topology(tail, aggregate_public, pins, observer_pin, now)
    paths = document(root, "topology", topology, root_private)
    checked, topology_sha = verify_topology(root_public, *paths, "9" * 64, config, now=now)
    if checked != topology:
        msg = "The verified topology differs from the signed topology."
        raise RuntimeError(msg)
    return AggregateFixture(
        root_public=root_public,
        aggregate_private=aggregate_private,
        aggregate_public=aggregate_public,
        observer_private=observer_private,
        observer_public=observer_public,
        config=config,
        topology=checked,
        topology_sha=topology_sha,
        envelopes=envelopes,
        role_keys=role_keys,
        observation=_observation(observer_pin, fenced, nonce, report_sha, now),
        fenced=fenced,
        nonce=nonce,
        report_sha=report_sha,
    )
