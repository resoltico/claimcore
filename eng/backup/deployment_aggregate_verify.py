"""Recheck the exact six-observer signed deployment aggregate."""

import base64
import hashlib
import json
from datetime import datetime, timedelta, timezone

from deployment_aggregate import (
    FIELDS,
    OBSERVER_FIELDS,
    PROBE_FIELDS,
    _decode_entry,
    _digest,
)
from deployment_common import canonical, private_path, require, timestamp, verify
from deployment_observer import verify_observer
from deployment_topology import ROLES

AVAILABILITY = {
    "primary": "database-read",
    "witness": "database-read",
    "archive": "retained-copy",
    "checkpoint": "retained-copy",
    "key": "key-possession",
}


def verify_aggregate(
    raw,
    signature,
    public_key,
    topology,
    topology_sha,
    publication_sha,
    report_sha,
    fenced,
    role_keys,
    observer_key,
    *,
    now=None,
    required_scope="full",
):
    require(
        isinstance(raw, bytes)
        and 0 < len(raw) <= 131072
        and raw == canonical(json.loads(raw)),
        "aggregate-canonical",
    )
    require(
        isinstance(signature, bytes) and len(signature) == 64,
        "aggregate-signature-size",
    )
    key = private_path(public_key)
    require(
        hashlib.sha256(key.read_bytes()).hexdigest()
        == topology["deploymentVerifierPublicKeySha256"],
        "aggregate-signer-pin",
    )
    proof, digest = verify(
        {
            "report": json.loads(raw),
            "signatureBase64": base64.b64encode(signature).decode("ascii"),
        },
        key,
    )
    require(
        set(proof) == FIELDS
        and proof["format"] == "claimcore-independent-host-proof-1"
        and proof["source"] == "ClaimCore.DeploymentVerifier",
        "aggregate-shape",
    )
    require(
        proof["scope"] == required_scope and proof["realDataReady"] is False,
        "aggregate-scope",
    )
    tail = fenced["tail"]
    for name, expected in (
        ("installationId", tail["installationId"]),
        ("lineageId", tail["lineageId"]),
        ("epoch", tail["epoch"]),
        ("writerGeneration", tail["newGeneration"]),
        ("reportSha256", report_sha),
        ("fenceReportSha256", fenced["fenceSha256"]),
        ("supplementSha256", fenced["tailSha256"]),
        ("finalWalObjectSha256", fenced["finalWalObjectSha256"]),
        ("w1Sequence", tail["w1Sequence"]),
        ("w1Hash", tail["w1Hash"]),
        ("publicationManifestSha256", publication_sha),
        ("topologyManifestSha256", topology_sha),
        ("signingKeyId", topology["deploymentVerifierSigningKeyId"]),
        ("signerHolderActorId", topology["deploymentVerifierHolderActorId"]),
    ):
        require(proof[name] == expected, "aggregate-link")
    require(
        _digest(proof["nonce"]) and _digest(proof["verifierBinarySha256"]),
        "aggregate-identity",
    )
    checked, until = timestamp(proof["checkedAt"]), timestamp(proof["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(
        checked <= now < until <= checked + timedelta(seconds=90), "aggregate-expired"
    )
    entries = proof["probes"]
    require(
        isinstance(entries, list)
        and [item.get("role") for item in entries] == list(ROLES),
        "aggregate-roles",
    )
    require(
        hashlib.sha256(canonical(entries)).hexdigest() == proof["probeSetSha256"],
        "aggregate-probe-set",
    )
    for role, entry, pin in zip(ROLES, entries, topology["rolePins"], strict=True):
        key = private_path(role_keys[role])
        require(
            pin["role"] == role
            and hashlib.sha256(key.read_bytes()).hexdigest()
            == pin["probePublicKeySha256"],
            "aggregate-role-key",
        )
        envelope = _decode_entry(entry, PROBE_FIELDS, "probeSha256")
        report, _ = verify(envelope, key)
        require(
            report["role"] == role
            and report["nonce"] == proof["nonce"]
            and report["qualificationSha256"] == fenced["tailSha256"],
            "aggregate-probe-link",
        )
        require(
            report.get("format") == "claimcore-deployment-probe-1"
            and report.get("availabilityKind") == AVAILABILITY[role]
            and report.get("available") is True
            and report.get("containerized") is False,
            "aggregate-probe-unavailable",
        )
        issued = timestamp(report.get("issuedAt"))
        expires = timestamp(report.get("expiresAt"))
        require(
            entry["checkedAt"] == report["issuedAt"]
            and entry["validUntil"] == report["expiresAt"],
            "aggregate-probe-summary",
        )
        require(
            abs((now - issued).total_seconds()) <= 30
            and issued < expires <= issued + timedelta(seconds=90)
            and now < expires,
            "aggregate-probe-expired",
        )
        for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId"):
            require(report[name] == pin[name], "aggregate-role-pin")
        if role == "archive":
            require(
                report.get("finalWalObjects") == tail["walObjects"]
                and report.get("finalWalObjectCount") == len(tail["walObjects"])
                and report.get("finalWalObjectSha256")
                == fenced["finalWalObjectSha256"],
                "aggregate-final-wal",
            )
    observer = _decode_entry(
        proof["oldWriterFenceObservation"], OBSERVER_FIELDS, "observationSha256"
    )
    observed_key = private_path(observer_key)
    require(
        hashlib.sha256(observed_key.read_bytes()).hexdigest()
        == topology["fenceObserverPin"]["probePublicKeySha256"],
        "aggregate-observer-key",
    )
    verify_observer(
        observer,
        observed_key,
        topology["fenceObserverPin"],
        proof["nonce"],
        report_sha,
        fenced["fenceSha256"],
        tail,
        fenced["fence"],
        now=now,
    )
    return proof, digest
