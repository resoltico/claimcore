"""Shared shape of the signed six-observer deployment aggregate."""

import base64
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

from backup_types import JsonObject
from deployment_common import SIGNATURE_BYTES, canonical, require

ENTRY_LIMIT = 16384

FIELDS = {
    "format",
    "source",
    "scope",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "nonce",
    "reportSha256",
    "fenceReportSha256",
    "supplementSha256",
    "finalWalObjectSha256",
    "w1Sequence",
    "w1Hash",
    "publicationManifestSha256",
    "topologyManifestSha256",
    "verifierBinarySha256",
    "probeSetSha256",
    "probes",
    "oldWriterFenceObservation",
    "checkedAt",
    "validUntil",
    "signingKeyId",
    "signerHolderActorId",
    "realDataReady",
}
PROBE_FIELDS = {
    "role",
    "canonicalBase64",
    "signatureBase64",
    "probeSha256",
    "machineHash",
    "storageHash",
    "adminActorId",
    "hostKeyId",
    "checkedAt",
    "validUntil",
}
OBSERVER_FIELDS = PROBE_FIELDS - {"probeSha256"} | {"observationSha256"}
AVAILABILITY = {
    "primary": "database-read",
    "witness": "database-read",
    "archive": "retained-copy",
    "checkpoint": "retained-copy",
    "key": "key-possession",
}


@dataclass(frozen=True)
class AggregateContext:
    """The reviewed documents and keys an aggregate is bound to."""

    topology: JsonObject
    topology_sha: str
    publication_sha: str
    report_sha: str
    fenced: JsonObject
    role_keys: dict[str, Path]
    observer_key: Path


def raw_entry(role: str, envelope: JsonObject, hash_name: str) -> JsonObject:
    """Wrap one signed role report as an aggregate entry."""
    body = canonical(envelope["report"])
    signature = base64.b64decode(envelope["signatureBase64"], validate=True)
    require(len(signature) == SIGNATURE_BYTES and len(body) <= ENTRY_LIMIT, "aggregate-probe-size")
    report = envelope["report"]
    return {
        "role": role,
        "canonicalBase64": base64.b64encode(body).decode("ascii"),
        "signatureBase64": envelope["signatureBase64"],
        hash_name: hashlib.sha256(body).hexdigest(),
        "machineHash": report["machineHash"],
        "storageHash": report["storageHash"],
        "adminActorId": report["adminActorId"],
        "hostKeyId": report["hostKeyId"],
        "checkedAt": report.get("checkedAt", report.get("issuedAt")),
        "validUntil": report.get("validUntil", report.get("expiresAt")),
    }


def decode_entry(entry: JsonObject, fields: set[str], hash_name: str) -> JsonObject:
    """Recover a signed envelope from an aggregate entry, checking every summary field."""
    require(isinstance(entry, dict) and set(entry) == fields, "aggregate-probe-shape")
    raw = base64.b64decode(entry["canonicalBase64"], validate=True)
    signed = base64.b64decode(entry["signatureBase64"], validate=True)
    require(0 < len(raw) <= ENTRY_LIMIT and len(signed) == SIGNATURE_BYTES, "aggregate-probe-size")
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document),
        "aggregate-probe-canonical",
    )
    require(hashlib.sha256(raw).hexdigest() == entry[hash_name], "aggregate-probe-digest")
    for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(document.get(name) == entry[name], "aggregate-probe-summary")
    return {"report": document, "signatureBase64": entry["signatureBase64"]}
