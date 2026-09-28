"""Signed six-observer deployment evidence; product still decides readiness."""

import base64
import hashlib
import json
import re
from datetime import datetime, timedelta, timezone

from deployment_common import (
    canonical,
    private_path,
    require,
    sign,
    utc,
    verify,
)
from deployment_observer import verify_observer
from deployment_topology import ROLES

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


def _digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _raw_entry(role, envelope, hash_name):
    body = canonical(envelope["report"])
    signature = base64.b64decode(envelope["signatureBase64"], validate=True)
    require(len(signature) == 64 and len(body) <= 16384, "aggregate-probe-size")
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


def _decode_entry(entry, fields, hash_name):
    require(isinstance(entry, dict) and set(entry) == fields, "aggregate-probe-shape")
    raw = base64.b64decode(entry["canonicalBase64"], validate=True)
    signed = base64.b64decode(entry["signatureBase64"], validate=True)
    require(0 < len(raw) <= 16384 and len(signed) == 64, "aggregate-probe-size")
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document),
        "aggregate-probe-canonical",
    )
    require(
        hashlib.sha256(raw).hexdigest() == entry[hash_name], "aggregate-probe-digest"
    )
    for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(document.get(name) == entry[name], "aggregate-probe-summary")
    return {"report": document, "signatureBase64": entry["signatureBase64"]}


def make_aggregate(
    publication_sha,
    topology_sha,
    report_sha,
    fenced,
    nonce,
    envelopes,
    observer_envelope,
    topology,
    role_keys,
    observer_key,
    signing_key,
    signing_public_key,
    verifier_binary_sha,
    *,
    scope="synthetic-only",
    now=None,
):
    """Construct signed evidence only after raw role signatures are independently checked."""
    require(scope in ("full", "synthetic-only") and _digest(nonce), "aggregate-scope")
    public = private_path(signing_public_key)
    require(
        hashlib.sha256(public.read_bytes()).hexdigest()
        == topology["deploymentVerifierPublicKeySha256"],
        "aggregate-signer-pin",
    )
    tail = fenced["tail"]
    require(set(envelopes) == set(ROLES), "aggregate-roles")
    entries = []
    for role in ROLES:
        report, _ = verify(envelopes[role], role_keys[role])
        require(
            report["role"] == role
            and report["nonce"] == nonce
            and report["qualificationSha256"] == fenced["tailSha256"],
            "aggregate-probe-link",
        )
        entries.append(_raw_entry(role, envelopes[role], "probeSha256"))
    observed, _ = verify_observer(
        observer_envelope,
        observer_key,
        topology["fenceObserverPin"],
        nonce,
        report_sha,
        fenced["fenceSha256"],
        tail,
        fenced["fence"],
    )
    require(observed["role"] == "old-writer-fence", "aggregate-observer")
    old_entry = _raw_entry("old-writer-fence", observer_envelope, "observationSha256")
    checked = now or datetime.now(timezone.utc)
    proof = {
        "format": "claimcore-independent-host-proof-1",
        "source": "ClaimCore.DeploymentVerifier",
        "scope": scope,
        "installationId": tail["installationId"],
        "lineageId": tail["lineageId"],
        "epoch": tail["epoch"],
        "writerGeneration": tail["newGeneration"],
        "nonce": nonce,
        "reportSha256": report_sha,
        "fenceReportSha256": fenced["fenceSha256"],
        "supplementSha256": fenced["tailSha256"],
        "finalWalObjectSha256": fenced["finalWalObjectSha256"],
        "w1Sequence": tail["w1Sequence"],
        "w1Hash": tail["w1Hash"],
        "publicationManifestSha256": publication_sha,
        "topologyManifestSha256": topology_sha,
        "verifierBinarySha256": verifier_binary_sha,
        "probeSetSha256": hashlib.sha256(canonical(entries)).hexdigest(),
        "probes": entries,
        "oldWriterFenceObservation": old_entry,
        "checkedAt": utc(checked),
        "validUntil": utc(checked + timedelta(seconds=60)),
        "signingKeyId": topology["deploymentVerifierSigningKeyId"],
        "signerHolderActorId": topology["deploymentVerifierHolderActorId"],
        "realDataReady": False,
    }
    envelope = sign(proof, signing_key)
    raw, signature = canonical(proof), base64.b64decode(envelope["signatureBase64"])
    from deployment_aggregate_verify import verify_aggregate

    verify_aggregate(
        raw,
        signature,
        public,
        topology,
        topology_sha,
        publication_sha,
        report_sha,
        fenced,
        role_keys,
        observer_key,
        now=checked,
        required_scope=scope,
    )
    return raw, signature
