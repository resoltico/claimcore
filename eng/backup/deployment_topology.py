"""Root-signed independent-host topology; owner config cannot choose its trust root."""

import base64
import hashlib
import json
import re
import tempfile
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from deployment_common import canonical, private_path, require, timestamp, verify

ROLES = ("archive", "checkpoint", "key", "primary", "witness")
FIELDS = {
    "format",
    "topologyId",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "w1Sequence",
    "w1Hash",
    "publicationManifestSha256",
    "deploymentVerifierSigningKeyId",
    "deploymentVerifierHolderActorId",
    "deploymentVerifierPublicKeySha256",
    "rolePins",
    "issuedAt",
    "validUntil",
    "fenceObserverPin",
}
PIN_FIELDS = {
    "role",
    "probePublicKeySha256",
    "machineHash",
    "storageHash",
    "adminActorId",
    "hostKeyId",
    "sshHostKeySha256",
}
FENCE_PIN_FIELDS = PIN_FIELDS | {
    "oldEndpointId",
    "oldEndpointAddressSha256",
    "oldPrimaryRoleOid",
    "oldWitnessRoleOid",
    "oldPrimaryCredentialSha256",
    "oldWitnessCredentialSha256",
    "primarySessionSetSha256",
    "witnessSessionSetSha256",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _root_verify(root_key, envelope):
    if isinstance(root_key, bytes):
        with tempfile.TemporaryDirectory(prefix="claimcore-topology-root-") as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            path = root / "root.pub"
            path.write_bytes(root_key)
            path.chmod(0o600)
            return verify(envelope, path)
    return verify(envelope, root_key)


def verify_topology(
    root_key, manifest_path, signature_path, publication_sha, config, now=None
):
    require(root_key is not None, "publication-root-unavailable")
    source, signature = private_path(manifest_path), private_path(signature_path)
    raw, signed = source.read_bytes(), signature.read_bytes()
    require(0 < len(raw) <= 65536 and len(signed) == 64, "topology-size")
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document), "topology-canonical"
    )
    manifest, digest = _root_verify(
        root_key,
        {
            "report": document,
            "signatureBase64": base64.b64encode(signed).decode("ascii"),
        },
    )
    require(
        set(manifest) == FIELDS
        and manifest["format"] == "claimcore-deployment-topology-1",
        "topology-shape",
    )
    for name in (
        "topologyId",
        "installationId",
        "lineageId",
        "deploymentVerifierSigningKeyId",
        "deploymentVerifierHolderActorId",
    ):
        require(_uuid(manifest[name]), "topology-identity")
    require(
        type(manifest["epoch"]) is int
        and manifest["epoch"] > 0
        and type(manifest["writerGeneration"]) is int
        and manifest["writerGeneration"] > 0
        and type(manifest["w1Sequence"]) is int
        and manifest["w1Sequence"] > 0
        and _sha(manifest["w1Hash"])
        and _sha(manifest["publicationManifestSha256"])
        and _sha(manifest["deploymentVerifierPublicKeySha256"])
        and manifest["publicationManifestSha256"] == publication_sha,
        "topology-authority",
    )
    issued, expires = timestamp(manifest["issuedAt"]), timestamp(manifest["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(issued <= now < expires <= issued + timedelta(days=7), "topology-expired")
    pins = manifest["rolePins"]
    require(isinstance(pins, list) and len(pins) == 5, "topology-roles")
    require(
        [item.get("role") for item in pins if isinstance(item, dict)] == list(ROLES),
        "topology-roles",
    )
    for pin in pins:
        require(set(pin) == PIN_FIELDS, "topology-role-shape")
        for name in (
            "probePublicKeySha256",
            "machineHash",
            "storageHash",
            "sshHostKeySha256",
        ):
            require(_sha(pin[name]), "topology-role-digest")
        for name in ("adminActorId", "hostKeyId"):
            require(_uuid(pin[name]), "topology-role-identity")
        owner_pin = config["roles"][pin["role"]]
        public = private_path(owner_pin["probePublicKey"])
        require(
            hashlib.sha256(public.read_bytes()).hexdigest()
            == pin["probePublicKeySha256"],
            "topology-probe-key",
        )
        for name in (
            "machineHash",
            "storageHash",
            "adminActorId",
            "hostKeyId",
            "sshHostKeySha256",
        ):
            require(owner_pin[name] == pin[name], "topology-owner-pin")
    for name in (
        "probePublicKeySha256",
        "machineHash",
        "storageHash",
        "adminActorId",
        "hostKeyId",
    ):
        require(
            len({pin[name] for pin in pins}) == len(ROLES), "topology-not-independent"
        )
    observer = manifest["fenceObserverPin"]
    require(
        isinstance(observer, dict) and set(observer) == FENCE_PIN_FIELDS,
        "topology-fence-observer",
    )
    require(
        observer["role"] == "old-writer-fence" and _uuid(observer["oldEndpointId"]),
        "topology-fence-endpoint",
    )
    for name in (
        "probePublicKeySha256",
        "machineHash",
        "storageHash",
        "sshHostKeySha256",
        "oldEndpointAddressSha256",
        "oldPrimaryCredentialSha256",
        "oldWitnessCredentialSha256",
        "primarySessionSetSha256",
        "witnessSessionSetSha256",
    ):
        require(_sha(observer[name]), "topology-fence-digest")
    for name in ("oldPrimaryRoleOid", "oldWitnessRoleOid"):
        require(
            type(observer[name]) is int and 0 < observer[name] <= 2**32 - 1,
            "topology-fence-role",
        )
    for name in ("adminActorId", "hostKeyId"):
        require(_uuid(observer[name]), "topology-fence-identity")
    owner_observer = config["fenceObserver"]
    require(owner_observer["role"] == observer["role"], "topology-fence-pin")
    key = private_path(owner_observer["probePublicKey"])
    require(
        hashlib.sha256(key.read_bytes()).hexdigest()
        == observer["probePublicKeySha256"],
        "topology-fence-key",
    )
    for name in FENCE_PIN_FIELDS - {"role", "probePublicKeySha256"}:
        require(owner_observer[name] == observer[name], "topology-fence-pin")
    for name in (
        "probePublicKeySha256",
        "machineHash",
        "storageHash",
        "adminActorId",
        "hostKeyId",
    ):
        require(
            observer[name] not in {pin[name] for pin in pins},
            "topology-fence-not-independent",
        )
    require(
        manifest["deploymentVerifierPublicKeySha256"]
        not in {pin["probePublicKeySha256"] for pin in pins}
        | {observer["probePublicKeySha256"]},
        "topology-verifier-not-independent",
    )
    return manifest, digest
