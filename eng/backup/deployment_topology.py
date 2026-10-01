"""Root-signed independent-host topology; owner config cannot choose its trust root."""

import base64
import hashlib
import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
    private_path,
    require,
    timestamp,
    verify_root_signed,
)

ROLES = ("archive", "checkpoint", "key", "primary", "witness")
MANIFEST_LIMIT = 65536
VALIDITY_DAYS = 7
MAX_ROLE_ID = 2**32 - 1
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


def _verify_authority(manifest: JsonObject, publication_sha: str, now: datetime | None) -> None:
    require(
        set(manifest) == FIELDS and manifest["format"] == "claimcore-deployment-topology-1",
        "topology-shape",
    )
    for name in (
        "topologyId",
        "installationId",
        "lineageId",
        "deploymentVerifierSigningKeyId",
        "deploymentVerifierHolderActorId",
    ):
        require(is_uuid(manifest[name]), "topology-identity")
    require(
        type(manifest["epoch"]) is int
        and manifest["epoch"] > 0
        and type(manifest["writerGeneration"]) is int
        and manifest["writerGeneration"] > 0
        and type(manifest["w1Sequence"]) is int
        and manifest["w1Sequence"] > 0
        and is_sha256(manifest["w1Hash"])
        and is_sha256(manifest["publicationManifestSha256"])
        and is_sha256(manifest["deploymentVerifierPublicKeySha256"])
        and manifest["publicationManifestSha256"] == publication_sha,
        "topology-authority",
    )
    issued, expires = timestamp(manifest["issuedAt"]), timestamp(manifest["validUntil"])
    current = now or datetime.now(UTC)
    require(
        issued <= current < expires <= issued + timedelta(days=VALIDITY_DAYS), "topology-expired"
    )


def _verify_role_pin(pin: JsonObject, config: JsonObject) -> None:
    require(set(pin) == PIN_FIELDS, "topology-role-shape")
    for name in ("probePublicKeySha256", "machineHash", "storageHash", "sshHostKeySha256"):
        require(is_sha256(pin[name]), "topology-role-digest")
    for name in ("adminActorId", "hostKeyId"):
        require(is_uuid(pin[name]), "topology-role-identity")
    owner_pin = config["roles"][pin["role"]]
    public = private_path(owner_pin["probePublicKey"])
    require(
        hashlib.sha256(public.read_bytes()).hexdigest() == pin["probePublicKeySha256"],
        "topology-probe-key",
    )
    for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId", "sshHostKeySha256"):
        require(owner_pin[name] == pin[name], "topology-owner-pin")


def _verify_role_pins(manifest: JsonObject, config: JsonObject) -> list[JsonObject]:
    pins: list[JsonObject] = manifest["rolePins"]
    require(isinstance(pins, list) and len(pins) == len(ROLES), "topology-roles")
    require(
        [item.get("role") for item in pins if isinstance(item, dict)] == list(ROLES),
        "topology-roles",
    )
    for pin in pins:
        _verify_role_pin(pin, config)
    for name in ("probePublicKeySha256", "machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(len({pin[name] for pin in pins}) == len(ROLES), "topology-not-independent")
    return pins


def _verify_observer_shape(observer: JsonObject) -> None:
    require(
        isinstance(observer, dict) and set(observer) == FENCE_PIN_FIELDS,
        "topology-fence-observer",
    )
    require(
        observer["role"] == "old-writer-fence" and is_uuid(observer["oldEndpointId"]),
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
        require(is_sha256(observer[name]), "topology-fence-digest")
    for name in ("oldPrimaryRoleOid", "oldWitnessRoleOid"):
        require(
            type(observer[name]) is int and 0 < observer[name] <= MAX_ROLE_ID,
            "topology-fence-role",
        )
    for name in ("adminActorId", "hostKeyId"):
        require(is_uuid(observer[name]), "topology-fence-identity")


def _verify_observer_pin(
    manifest: JsonObject, config: JsonObject, pins: list[JsonObject]
) -> JsonObject:
    observer: JsonObject = manifest["fenceObserverPin"]
    _verify_observer_shape(observer)
    owner_observer = config["fenceObserver"]
    require(owner_observer["role"] == observer["role"], "topology-fence-pin")
    key = private_path(owner_observer["probePublicKey"])
    require(
        hashlib.sha256(key.read_bytes()).hexdigest() == observer["probePublicKeySha256"],
        "topology-fence-key",
    )
    for name in FENCE_PIN_FIELDS - {"role", "probePublicKeySha256"}:
        require(owner_observer[name] == observer[name], "topology-fence-pin")
    for name in ("probePublicKeySha256", "machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(
            observer[name] not in {pin[name] for pin in pins},
            "topology-fence-not-independent",
        )
    return observer


def verify_topology(
    root_key: bytes | str | Path | None,
    manifest_path: str | Path,
    signature_path: str | Path,
    publication_sha: str,
    config: JsonObject,
    *,
    now: datetime | None = None,
) -> tuple[JsonObject, str]:
    """Verify the root-signed topology against the owner configuration."""
    if root_key is None:
        msg = "publication-root-unavailable"
        raise DeploymentRefusalError(msg)
    source, signature = private_path(manifest_path), private_path(signature_path)
    raw, signed = source.read_bytes(), signature.read_bytes()
    require(0 < len(raw) <= MANIFEST_LIMIT and len(signed) == SIGNATURE_BYTES, "topology-size")
    document = json.loads(raw)
    require(isinstance(document, dict) and raw == canonical(document), "topology-canonical")
    manifest, digest = verify_root_signed(
        root_key,
        {"report": document, "signatureBase64": base64.b64encode(signed).decode("ascii")},
    )
    _verify_authority(manifest, publication_sha, now)
    pins = _verify_role_pins(manifest, config)
    observer = _verify_observer_pin(manifest, config, pins)
    require(
        manifest["deploymentVerifierPublicKeySha256"]
        not in {pin["probePublicKeySha256"] for pin in pins} | {observer["probePublicKeySha256"]},
        "topology-verifier-not-independent",
    )
    return manifest, digest
