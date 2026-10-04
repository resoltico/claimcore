"""Same-host, forged-probe and shared-custody negatives for deployment admission."""

import base64
import copy
import hashlib
import uuid
from dataclasses import replace
from functools import partial

from backup_test_support import ensure, refuses
from backup_types import JsonObject
from deployment_admission_fixture import SAME_MACHINE, Deployment, report
from deployment_common import sign
from deployment_topology import ROLES
from deployment_trust import ProbeExpectation, evaluate

STALE_ISSUE = "2000-01-01T00:00:00Z"
STALE_EXPIRY = "2000-01-01T00:01:00Z"
SIGNATURE_BYTES = 64


def evaluate_deployment(
    deployment: Deployment,
    *,
    config: JsonObject | None = None,
    envelopes: dict[str, JsonObject] | None = None,
    expect: ProbeExpectation | None = None,
    live_transport: bool = True,
) -> dict[str, bool | str]:
    """Evaluate the deployment with optional replacements for its inputs."""
    return evaluate(
        config or deployment.config,
        envelopes or deployment.envelopes,
        expect or deployment.expect,
        live_transport=live_transport,
    )


def _baseline_refusals(deployment: Deployment) -> None:
    refuses(lambda: evaluate_deployment(deployment), "custody-not-independent")
    missing = {role: value for role, value in deployment.envelopes.items() if role != "witness"}
    refuses(lambda: evaluate_deployment(deployment, envelopes=missing), "deployment-roles")
    forged = dict(deployment.envelopes)
    forged["witness"] = {
        **forged["witness"],
        "signatureBase64": base64.b64encode(b"\0" * SIGNATURE_BYTES).decode("ascii"),
    }
    refuses(
        lambda: evaluate_deployment(deployment, envelopes=forged),
        "deployment-signature-invalid",
    )
    stale_challenge = replace(deployment.expect, nonce="d" * 64)
    refuses(lambda: evaluate_deployment(deployment, expect=stale_challenge), "probe-challenge")


def _signed_with_change(deployment: Deployment, role: str, field: str, value: object) -> JsonObject:
    body = dict(deployment.envelopes[role]["report"])
    body[field] = value
    if field == "issuedAt":
        body["expiresAt"] = STALE_EXPIRY
    return sign(body, deployment.keys[role])


def _tampered_probe_refusals(deployment: Deployment) -> None:
    for role, field, value, category in (
        ("primary", "issuedAt", STALE_ISSUE, "probe-not-fresh"),
        ("primary", "containerized", True, "container-not-independent-host"),
        ("key", "keyChallengeProofSha256", "f" * 64, "key-custody-proof"),
        ("witness", "installationId", str(uuid.uuid4()), "probe-installation-mismatch"),
        ("archive", "objectSha256", "f" * 64, "probe-object-unrelated"),
        (
            "archive",
            "finalWalObjects",
            deployment.qualified["finalWalObjects"][:-1],
            "probe-final-wal-incomplete",
        ),
    ):
        changed = dict(deployment.envelopes)
        changed[role] = _signed_with_change(deployment, role, field, value)
        refuses(partial(evaluate_deployment, deployment, envelopes=changed), category)


def _pin_mismatch_refusal(deployment: Deployment) -> None:
    primary = deployment.config["roles"]["primary"]
    primary["machineHash"] = "e" * 64
    refuses(lambda: evaluate_deployment(deployment), "probe-not-owner-pinned")
    primary["machineHash"] = SAME_MACHINE


def _distinct_hosts(deployment: Deployment) -> tuple[JsonObject, dict[str, JsonObject]]:
    """Build a local-test configuration whose roles sit on different machines and disks."""
    distinct = copy.deepcopy(deployment.config)
    distinct["mode"] = "local-test"
    envelopes = {}
    for number, role in enumerate(ROLES):
        pinned = distinct["roles"][role]
        pinned["machineHash"] = hashlib.sha256(str(number).encode()).hexdigest()
        pinned["storageHash"] = hashlib.sha256(("disk" + str(number)).encode()).hexdigest()
        envelopes[role] = sign(report(role, pinned, deployment.expect), deployment.keys[role])
    return distinct, envelopes


def _resigned_witness(
    deployment: Deployment,
    envelopes: dict[str, JsonObject],
    config: JsonObject,
    expect: ProbeExpectation,
) -> dict[str, JsonObject]:
    changed = dict(envelopes)
    changed["witness"] = sign(
        report("witness", config["roles"]["witness"], expect), deployment.keys["witness"]
    )
    return changed


def _shared_custody_refusals(
    deployment: Deployment, distinct: JsonObject, envelopes: dict[str, JsonObject]
) -> None:
    shared_admin = copy.deepcopy(distinct)
    shared_admin["mode"] = "production"
    shared_admin["roles"]["witness"]["adminActorId"] = shared_admin["roles"]["primary"][
        "adminActorId"
    ]
    shared_envelopes = _resigned_witness(deployment, envelopes, shared_admin, deployment.expect)
    refuses(
        lambda: evaluate_deployment(deployment, config=shared_admin, envelopes=shared_envelopes),
        "custody-not-independent",
    )
    same_cluster = copy.deepcopy(distinct)
    same_cluster["mode"] = "production"
    same_cluster["roles"]["witness"]["postgresSystemId"] = deployment.qualified["primarySystemId"]
    same_qualified = {
        **deployment.qualified,
        "witnessSystemId": deployment.qualified["primarySystemId"],
    }
    same_expect = replace(deployment.expect, qualification=same_qualified)
    same_envelopes = _resigned_witness(deployment, envelopes, same_cluster, same_expect)
    refuses(
        lambda: evaluate_deployment(
            deployment, config=same_cluster, envelopes=same_envelopes, expect=same_expect
        ),
        "clusters-not-independent",
    )


def trust_negatives(deployment: Deployment) -> None:
    """Every same-host, forged, stale and shared-custody probe set must be refused."""
    _baseline_refusals(deployment)
    _tampered_probe_refusals(deployment)
    _pin_mismatch_refusal(deployment)
    distinct, envelopes = _distinct_hosts(deployment)
    _shared_custody_refusals(deployment, distinct, envelopes)
    refuses(
        lambda: evaluate_deployment(
            deployment, config=distinct, envelopes=envelopes, live_transport=False
        ),
        "local-only-qualification",
    )
    # A copied software age identity can prove possession, never non-copyability or readiness.
    corroborated = evaluate_deployment(
        deployment, config={**distinct, "mode": "production"}, envelopes=envelopes
    )
    ensure(corroborated["realDataReady"] is False, "software-key-is-not-readiness")
