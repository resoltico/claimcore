"""Recheck the exact six-observer signed deployment aggregate."""

import base64
import hashlib
import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_aggregate_model import (
    AVAILABILITY,
    FIELDS,
    OBSERVER_FIELDS,
    PROBE_FIELDS,
    AggregateContext,
    decode_entry,
)
from deployment_common import (
    SIGNATURE_BYTES,
    canonical,
    is_sha256,
    private_path,
    require,
    timestamp,
    verify,
)
from deployment_observer import ObserverExpectation, verify_observer
from deployment_topology import ROLES

AGGREGATE_LIMIT = 131072
MAX_VALIDITY_SECONDS = 90
MAX_SKEW_SECONDS = 30


def _verify_signature(
    raw: bytes, signature: bytes, public_key: str | Path, context: AggregateContext
) -> tuple[JsonObject, str]:
    require(
        isinstance(raw, bytes)
        and 0 < len(raw) <= AGGREGATE_LIMIT
        and raw == canonical(json.loads(raw)),
        "aggregate-canonical",
    )
    require(
        isinstance(signature, bytes) and len(signature) == SIGNATURE_BYTES,
        "aggregate-signature-size",
    )
    key = private_path(public_key)
    require(
        hashlib.sha256(key.read_bytes()).hexdigest()
        == context.topology["deploymentVerifierPublicKeySha256"],
        "aggregate-signer-pin",
    )
    return verify(
        {
            "report": json.loads(raw),
            "signatureBase64": base64.b64encode(signature).decode("ascii"),
        },
        key,
    )


def _verify_links(proof: JsonObject, context: AggregateContext, required_scope: str) -> None:
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
    tail, fenced, topology = context.fenced["tail"], context.fenced, context.topology
    for name, expected in (
        ("installationId", tail["installationId"]),
        ("lineageId", tail["lineageId"]),
        ("epoch", tail["epoch"]),
        ("writerGeneration", tail["newGeneration"]),
        ("reportSha256", context.report_sha),
        ("fenceReportSha256", fenced["fenceSha256"]),
        ("supplementSha256", fenced["tailSha256"]),
        ("finalWalObjectSha256", fenced["finalWalObjectSha256"]),
        ("w1Sequence", tail["w1Sequence"]),
        ("w1Hash", tail["w1Hash"]),
        ("publicationManifestSha256", context.publication_sha),
        ("topologyManifestSha256", context.topology_sha),
        ("signingKeyId", topology["deploymentVerifierSigningKeyId"]),
        ("signerHolderActorId", topology["deploymentVerifierHolderActorId"]),
    ):
        require(proof[name] == expected, "aggregate-link")
    require(
        is_sha256(proof["nonce"]) and is_sha256(proof["verifierBinarySha256"]),
        "aggregate-identity",
    )


def _verify_probe(
    role: str, entry: JsonObject, pin: JsonObject, proof: JsonObject, context: AggregateContext
) -> tuple[JsonObject, datetime, datetime]:
    key = private_path(context.role_keys[role])
    require(
        pin["role"] == role
        and hashlib.sha256(key.read_bytes()).hexdigest() == pin["probePublicKeySha256"],
        "aggregate-role-key",
    )
    report, _ = verify(decode_entry(entry, PROBE_FIELDS, "probeSha256"), key)
    require(
        report["role"] == role
        and report["nonce"] == proof["nonce"]
        and report["qualificationSha256"] == context.fenced["tailSha256"],
        "aggregate-probe-link",
    )
    require(
        report.get("format") == "claimcore-deployment-probe-1"
        and report.get("availabilityKind") == AVAILABILITY[role]
        and report.get("available") is True
        and report.get("containerized") is False,
        "aggregate-probe-unavailable",
    )
    issued, expires = timestamp(report.get("issuedAt")), timestamp(report.get("expiresAt"))
    require(
        entry["checkedAt"] == report["issuedAt"] and entry["validUntil"] == report["expiresAt"],
        "aggregate-probe-summary",
    )
    return report, issued, expires


def _verify_probe_pin(
    report: JsonObject, pin: JsonObject, times: tuple[datetime, datetime], now: datetime
) -> None:
    issued, expires = times
    require(
        abs((now - issued).total_seconds()) <= MAX_SKEW_SECONDS
        and issued < expires <= issued + timedelta(seconds=MAX_VALIDITY_SECONDS)
        and now < expires,
        "aggregate-probe-expired",
    )
    for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(report[name] == pin[name], "aggregate-role-pin")


def _verify_probes(proof: JsonObject, context: AggregateContext, now: datetime) -> None:
    entries = proof["probes"]
    require(
        isinstance(entries, list) and [item.get("role") for item in entries] == list(ROLES),
        "aggregate-roles",
    )
    require(
        hashlib.sha256(canonical(entries)).hexdigest() == proof["probeSetSha256"],
        "aggregate-probe-set",
    )
    tail = context.fenced["tail"]
    for role, entry, pin in zip(ROLES, entries, context.topology["rolePins"], strict=True):
        report, issued, expires = _verify_probe(role, entry, pin, proof, context)
        _verify_probe_pin(report, pin, (issued, expires), now)
        if role == "archive":
            require(
                report.get("finalWalObjects") == tail["walObjects"]
                and report.get("finalWalObjectCount") == len(tail["walObjects"])
                and report.get("finalWalObjectSha256") == context.fenced["finalWalObjectSha256"],
                "aggregate-final-wal",
            )


def _verify_observation(proof: JsonObject, context: AggregateContext, now: datetime) -> None:
    pin = context.topology["fenceObserverPin"]
    observer = decode_entry(
        proof["oldWriterFenceObservation"], OBSERVER_FIELDS, "observationSha256"
    )
    observed_key = private_path(context.observer_key)
    require(
        hashlib.sha256(observed_key.read_bytes()).hexdigest() == pin["probePublicKeySha256"],
        "aggregate-observer-key",
    )
    verify_observer(
        observer,
        observed_key,
        ObserverExpectation(
            pin,
            proof["nonce"],
            context.report_sha,
            context.fenced["fenceSha256"],
            context.fenced["tail"],
            context.fenced["fence"],
        ),
        now=now,
    )


def verify_aggregate(
    raw: bytes,
    signature: bytes,
    public_key: str | Path,
    context: AggregateContext,
    *,
    now: datetime | None = None,
    required_scope: str = "full",
) -> tuple[JsonObject, str]:
    """Recheck signature, links, freshness, every probe and the old-writer observation."""
    proof, digest = _verify_signature(raw, signature, public_key, context)
    _verify_links(proof, context, required_scope)
    checked, until = timestamp(proof["checkedAt"]), timestamp(proof["validUntil"])
    current = now or datetime.now(UTC)
    require(
        checked <= current < until <= checked + timedelta(seconds=MAX_VALIDITY_SECONDS),
        "aggregate-expired",
    )
    _verify_probes(proof, context, current)
    _verify_observation(proof, context, current)
    return proof, digest
