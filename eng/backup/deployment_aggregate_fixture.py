"""Synthetic six-host signed topology and observer fixture."""

import base64
import hashlib
import subprocess
import uuid
from datetime import timedelta

from deployment_common import canonical, sign, utc
from deployment_fenced_objects import final_objects_digest
from deployment_topology import ROLES, verify_topology


def keypair(root, name):
    private, public = root / (name + ".key"), root / (name + ".pub")
    for arguments in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        assert (
            subprocess.run(
                arguments,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            ).returncode
            == 0
        )
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def document(root, name, value, key):
    envelope = sign(value, key)
    raw, signed = root / (name + ".json"), root / (name + ".sig")
    raw.write_bytes(canonical(value))
    signed.write_bytes(base64.b64decode(envelope["signatureBase64"]))
    raw.chmod(0o600)
    signed.chmod(0o600)
    return raw, signed


def fixture(root, now):
    root_private, root_public = keypair(root, "publication-root")
    aggregate_private, aggregate_public = keypair(root, "aggregate")
    observer_private, observer_public = keypair(root, "observer")
    nonce, report_sha, fence_sha, tail_sha = (character * 64 for character in "abcd")
    installation, lineage = str(uuid.uuid4()), str(uuid.uuid4())
    tail = {
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
                "ciphertextBytes": 16 * 1024 * 1024 + 32,
                "walSegment": "000000010000000000000001",
                "walSegmentBytes": 16 * 1024 * 1024,
            }
            for number, cluster in enumerate(("PRIMARY", "WITNESS"), 1)
        ],
    }
    fenced = {
        "tail": tail,
        "fenceSha256": fence_sha,
        "tailSha256": tail_sha,
        "finalWalObjectSha256": final_objects_digest(tail),
    }
    config, pins, envelopes, role_keys = {"roles": {}}, [], {}, {}
    availability = {
        "primary": "database-read",
        "witness": "database-read",
        "archive": "retained-copy",
        "checkpoint": "retained-copy",
        "key": "key-possession",
    }
    for number, role in enumerate(ROLES, 1):
        key, public = keypair(root, role)
        role_keys[role] = public
        pin = {
            "role": role,
            "probePublicKeySha256": hashlib.sha256(public.read_bytes()).hexdigest(),
            "machineHash": f"{number:064x}",
            "storageHash": f"{number + 10:064x}",
            "adminActorId": str(uuid.uuid4()),
            "hostKeyId": str(uuid.uuid4()),
            "sshHostKeySha256": f"{number + 20:064x}",
        }
        pins.append(pin)
        config["roles"][role] = {**pin, "probePublicKey": str(public)}
        report = {
            "format": "claimcore-deployment-probe-1",
            "role": role,
            "nonce": nonce,
            "qualificationSha256": tail_sha,
            "availabilityKind": availability[role],
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
            report.update(
                finalWalObjects=tail["walObjects"],
                finalWalObjectCount=len(tail["walObjects"]),
                finalWalObjectSha256=fenced["finalWalObjectSha256"],
            )
        envelopes[role] = sign(report, key)
    observer_pin = {
        "role": "old-writer-fence",
        "oldEndpointId": str(uuid.uuid4()),
        "oldEndpointAddressSha256": "1" * 64,
        "oldPrimaryRoleOid": 101,
        "oldWitnessRoleOid": 102,
        "oldPrimaryCredentialSha256": "2" * 64,
        "oldWitnessCredentialSha256": "3" * 64,
        "primarySessionSetSha256": "4" * 64,
        "witnessSessionSetSha256": "5" * 64,
        "probePublicKeySha256": hashlib.sha256(
            observer_public.read_bytes()
        ).hexdigest(),
        "machineHash": "6" * 64,
        "storageHash": "7" * 64,
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        "sshHostKeySha256": "8" * 64,
    }
    config["fenceObserver"] = {**observer_pin, "probePublicKey": str(observer_public)}
    fenced["fence"] = {
        name: observer_pin[name]
        for name in (
            "oldEndpointId",
            "oldEndpointAddressSha256",
            "oldPrimaryRoleOid",
            "oldWitnessRoleOid",
            "oldPrimaryCredentialSha256",
            "oldWitnessCredentialSha256",
            "primarySessionSetSha256",
            "witnessSessionSetSha256",
        )
    }
    observation = {
        "format": "claimcore-old-writer-fence-observation-1",
        "role": "old-writer-fence",
        "nonce": nonce,
        "installationId": installation,
        "lineageId": lineage,
        "epoch": 1,
        "oldGeneration": 1,
        "newGeneration": 2,
        "w1Sequence": 11,
        "w1Hash": tail["w1Hash"],
        "reportSha256": report_sha,
        "fenceReportSha256": fence_sha,
        **fenced["fence"],
        "routeClosed": True,
        "primarySessionsZero": True,
        "witnessSessionsZero": True,
        "primaryCredentialDenied": True,
        "witnessCredentialDenied": True,
        "containerized": False,
        "machineHash": observer_pin["machineHash"],
        "storageHash": observer_pin["storageHash"],
        "adminActorId": observer_pin["adminActorId"],
        "hostKeyId": observer_pin["hostKeyId"],
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(seconds=60)),
    }
    topology = {
        "format": "claimcore-deployment-topology-1",
        "topologyId": str(uuid.uuid4()),
        "installationId": installation,
        "lineageId": lineage,
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
        "fenceObserverPin": observer_pin,
        "issuedAt": utc(now),
        "validUntil": utc(now + timedelta(hours=1)),
    }
    paths = document(root, "topology", topology, root_private)
    checked, topology_sha = verify_topology(
        root_public, *paths, "9" * 64, config, now=now
    )
    assert checked == topology
    return (
        root_public,
        aggregate_private,
        aggregate_public,
        observer_private,
        observer_public,
        config,
        checked,
        topology_sha,
        envelopes,
        role_keys,
        observation,
        fenced,
        nonce,
        report_sha,
    )
