"""Signed six-observer deployment evidence; product still decides readiness."""

import base64
import hashlib
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_aggregate_model import AggregateContext, raw_entry
from deployment_aggregate_verify import verify_aggregate
from deployment_common import canonical, is_sha256, private_path, require, sign, utc, verify
from deployment_observer import ObserverExpectation, verify_observer
from deployment_topology import ROLES

PROOF_SECONDS = 60


@dataclass(frozen=True)
class AggregateSubmission:
    """The signed probe reports and observation an aggregate is built from."""

    nonce: str
    envelopes: dict[str, JsonObject]
    observer_envelope: JsonObject
    verifier_binary_sha: str


def _role_entries(context: AggregateContext, submission: AggregateSubmission) -> list[JsonObject]:
    require(set(submission.envelopes) == set(ROLES), "aggregate-roles")
    entries = []
    for role in ROLES:
        report, _ = verify(submission.envelopes[role], context.role_keys[role])
        require(
            report["role"] == role
            and report["nonce"] == submission.nonce
            and report["qualificationSha256"] == context.fenced["tailSha256"],
            "aggregate-probe-link",
        )
        entries.append(raw_entry(role, submission.envelopes[role], "probeSha256"))
    return entries


def _observer_entry(context: AggregateContext, submission: AggregateSubmission) -> JsonObject:
    observed, _ = verify_observer(
        submission.observer_envelope,
        context.observer_key,
        ObserverExpectation(
            context.topology["fenceObserverPin"],
            submission.nonce,
            context.report_sha,
            context.fenced["fenceSha256"],
            context.fenced["tail"],
            context.fenced["fence"],
        ),
    )
    require(observed["role"] == "old-writer-fence", "aggregate-observer")
    return raw_entry("old-writer-fence", submission.observer_envelope, "observationSha256")


def _proof(
    context: AggregateContext,
    submission: AggregateSubmission,
    entries: list[JsonObject],
    scope: str,
    checked: datetime,
) -> JsonObject:
    tail, fenced, topology = context.fenced["tail"], context.fenced, context.topology
    return {
        "format": "claimcore-independent-host-proof-1",
        "source": "ClaimCore.DeploymentVerifier",
        "scope": scope,
        "installationId": tail["installationId"],
        "lineageId": tail["lineageId"],
        "epoch": tail["epoch"],
        "writerGeneration": tail["newGeneration"],
        "nonce": submission.nonce,
        "reportSha256": context.report_sha,
        "fenceReportSha256": fenced["fenceSha256"],
        "supplementSha256": fenced["tailSha256"],
        "finalWalObjectSha256": fenced["finalWalObjectSha256"],
        "w1Sequence": tail["w1Sequence"],
        "w1Hash": tail["w1Hash"],
        "publicationManifestSha256": context.publication_sha,
        "topologyManifestSha256": context.topology_sha,
        "verifierBinarySha256": submission.verifier_binary_sha,
        "probeSetSha256": hashlib.sha256(canonical(entries)).hexdigest(),
        "probes": entries,
        "oldWriterFenceObservation": _observer_entry(context, submission),
        "checkedAt": utc(checked),
        "validUntil": utc(checked + timedelta(seconds=PROOF_SECONDS)),
        "signingKeyId": topology["deploymentVerifierSigningKeyId"],
        "signerHolderActorId": topology["deploymentVerifierHolderActorId"],
        "realDataReady": False,
    }


def make_aggregate(
    context: AggregateContext,
    submission: AggregateSubmission,
    signing_key: str | Path,
    signing_public_key: str | Path,
    *,
    scope: str = "synthetic-only",
    now: datetime | None = None,
) -> tuple[bytes, bytes]:
    """Construct signed evidence only after raw role signatures are independently checked."""
    require(scope in ("full", "synthetic-only") and is_sha256(submission.nonce), "aggregate-scope")
    public = private_path(signing_public_key)
    require(
        hashlib.sha256(public.read_bytes()).hexdigest()
        == context.topology["deploymentVerifierPublicKeySha256"],
        "aggregate-signer-pin",
    )
    entries = _role_entries(context, submission)
    checked = now or datetime.now(UTC)
    proof = _proof(context, submission, entries, scope, checked)
    envelope = sign(proof, signing_key)
    raw, signature = canonical(proof), base64.b64decode(envelope["signatureBase64"])
    verify_aggregate(raw, signature, public, context, now=checked, required_scope=scope)
    return raw, signature
