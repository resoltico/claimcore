#!/usr/bin/env python3
"""Owner-only deploy-time independent-host gate; no cloud dependency."""

import argparse
import base64
import hashlib
import json
import os
import secrets
import subprocess
import sys
import uuid
from dataclasses import dataclass
from types import TracebackType

from backup_types import JsonObject

sys.dont_write_bytecode = True
from deployment_aggregate import AggregateSubmission, make_aggregate
from deployment_aggregate_io import write_aggregate
from deployment_aggregate_model import AggregateContext
from deployment_common import DeploymentRefusalError, private_path, require
from deployment_fenced import fenced_documents
from deployment_observer import ObserverExpectation, verify_observer
from deployment_product import (
    REVIEWED_PUBLICATION_ROOT_PEM,
    bounded,
    product_fenced_recheck,
)
from deployment_report import pre_handoff_report
from deployment_ssh import matching_text, ssh_command
from deployment_topology import ROLES, verify_topology
from deployment_trust import ProbeExpectation, evaluate

PROBE_OUTPUT_LIMIT = 16384
PROBE_TIMEOUT_SECONDS = 30
CHALLENGE_LIMIT = 4096
CHALLENGE_TIMEOUT_SECONDS = 10
CHALLENGE_BYTES = 32
MIN_PYTHON = (3, 12)
NONCE_BYTES = 32


def key_challenge(config: JsonObject) -> tuple[str, str]:
    """Encrypt a fresh secret to the key holder; return the challenge and the secret's digest."""
    recipient = matching_text(
        config["roles"]["key"]["ageRecipient"],
        r"age1[023456789acdefghjklmnpqrstuvwxyz]{58}",
        "key-recipient",
    )
    secret = secrets.token_bytes(CHALLENGE_BYTES)
    result = subprocess.run(
        ["age", "--encrypt", "--recipient", recipient],
        input=secret,
        capture_output=True,
        timeout=CHALLENGE_TIMEOUT_SECONDS,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= CHALLENGE_LIMIT,
        "key-challenge-encryption",
    )
    return base64.b64encode(result.stdout).decode("ascii"), hashlib.sha256(secret).hexdigest()


def _check_config(config: JsonObject) -> None:
    require(config.get("format") == "claimcore-deployment-1", "deployment-config-format")
    require(
        not any(
            name in config
            for name in ("productVerifierPath", "productRecheckCommand", "publicationRootPath")
        ),
        "config-selected-product-verifier",
    )
    require(config.get("mode") == "production", "local-only-qualification")
    installation = uuid.UUID(config["installationId"])
    require(
        installation.int != 0 and str(installation) == config["installationId"],
        "deployment-installation",
    )
    require(set(config.get("roles", {})) == set(ROLES), "deployment-roles")


def _check_topology_link(topology: JsonObject, tail: JsonObject) -> None:
    require(
        topology["installationId"] == tail["installationId"]
        and topology["lineageId"] == tail["lineageId"]
        and topology["epoch"] == tail["epoch"]
        and topology["writerGeneration"] == tail["newGeneration"]
        and topology["w1Sequence"] == tail["w1Sequence"]
        and topology["w1Hash"] == tail["w1Hash"],
        "topology-w1-link",
    )


def _probe_role_envelopes(
    config: JsonObject, nonce: str, fenced: JsonObject, challenge: str
) -> dict[str, JsonObject]:
    envelopes: dict[str, JsonObject] = {}
    for role in ROLES:
        command = ssh_command(config, role, nonce, fenced["tailSha256"], challenge)
        envelopes[role] = json.loads(bounded(command, PROBE_OUTPUT_LIMIT, PROBE_TIMEOUT_SECONDS))
    return envelopes


def _probe_old_writer(config: JsonObject, nonce: str, fenced: JsonObject) -> JsonObject:
    tail = fenced["tail"]
    command = ssh_command(
        config,
        "old-writer-fence",
        nonce,
        fenced["tailSha256"],
        None,
        pinned=config["fenceObserver"],
        extra=(
            "--fence-sha256",
            fenced["fenceSha256"],
            "--w1-sequence",
            str(tail["w1Sequence"]),
            "--w1-hash",
            tail["w1Hash"],
        ),
    )
    observer: JsonObject = json.loads(bounded(command, PROBE_OUTPUT_LIMIT, PROBE_TIMEOUT_SECONDS))
    return observer


def _probe_qualification(qualification: JsonObject, fenced: JsonObject) -> JsonObject:
    tail = fenced["tail"]
    return {
        **qualification,
        "witnessCutoff": tail["w1Sequence"],
        "witnessCutoffHash": tail["w1Hash"],
        "finalWalObjects": tail["walObjects"],
        "finalWalObjectSha256": fenced["finalWalObjectSha256"],
    }


def _sign_aggregate(
    config: JsonObject, context: AggregateContext, submission: AggregateSubmission
) -> tuple[bytes, bytes]:
    aggregate_key = os.environ.get("CLAIMCORE_DEPLOYMENT_AGGREGATE_SIGNING_KEY_FILE")
    require(aggregate_key is not None, "aggregate-signing-key-unavailable")
    return make_aggregate(
        context,
        submission,
        str(aggregate_key),
        config["aggregatePublicKeyFile"],
        scope="full",
    )


@dataclass(frozen=True)
class _Evidence:
    """The documents gathered and rechecked before any host is probed."""

    nonce: str
    qualification: JsonObject
    qualification_sha: str
    fenced: JsonObject
    product: JsonObject
    topology: JsonObject
    topology_sha: str


def _gather(config: JsonObject) -> _Evidence:
    qualification, qualification_sha = pre_handoff_report(config)
    nonce = secrets.token_hex(NONCE_BYTES)
    fenced = fenced_documents(config, qualification, qualification_sha)
    product = product_fenced_recheck(config, nonce, qualification_sha, qualification, fenced)
    require(not product["syntheticOnly"], "production-evidence-unavailable")
    topology, topology_sha = verify_topology(
        REVIEWED_PUBLICATION_ROOT_PEM,
        config["topologyManifestPath"],
        config["topologySignaturePath"],
        product["publicationSha256"],
        config,
    )
    _check_topology_link(topology, fenced["tail"])
    return _Evidence(
        nonce, qualification, qualification_sha, fenced, product, topology, topology_sha
    )


def _context(config: JsonObject, evidence: _Evidence) -> AggregateContext:
    return AggregateContext(
        topology=evidence.topology,
        topology_sha=evidence.topology_sha,
        publication_sha=evidence.product["publicationSha256"],
        report_sha=evidence.qualification_sha,
        fenced=evidence.fenced,
        role_keys={role: config["roles"][role]["probePublicKey"] for role in ROLES},
        observer_key=config["fenceObserver"]["probePublicKey"],
    )


def verify_deployment(config: JsonObject) -> JsonObject:
    """Run every independent-host check and write the signed aggregate."""
    _check_config(config)
    evidence = _gather(config)
    fenced, nonce = evidence.fenced, evidence.nonce
    challenge, key_proof = key_challenge(config)
    envelopes = _probe_role_envelopes(config, nonce, fenced, challenge)
    result = evaluate(
        config,
        envelopes,
        ProbeExpectation(
            nonce,
            fenced["tailSha256"],
            key_proof,
            _probe_qualification(evidence.qualification, fenced),
        ),
        live_transport=True,
    )
    old_observer = _probe_old_writer(config, nonce, fenced)
    _, observation_sha = verify_observer(
        old_observer,
        config["fenceObserver"]["probePublicKey"],
        ObserverExpectation(
            evidence.topology["fenceObserverPin"],
            nonce,
            evidence.qualification_sha,
            fenced["fenceSha256"],
            fenced["tail"],
            fenced["fence"],
        ),
    )
    submission = AggregateSubmission(
        nonce, envelopes, old_observer, config["productVerifierSha256"]
    )
    raw, signature = _sign_aggregate(config, _context(config, evidence), submission)
    write_aggregate(config, raw, signature)
    result["productRechecked"] = True
    result["publicationSha256"] = evidence.product["publicationSha256"]
    result["topologySha256"] = evidence.topology_sha
    result["oldWriterFenceObservationSha256"] = observation_sha
    result["aggregateSha256"] = hashlib.sha256(raw).hexdigest()
    return result


def main() -> None:
    """Run the deployment gate and write its signed aggregate."""
    os.umask(0o077)
    require(sys.version_info >= MIN_PYTHON, "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    config = json.loads(private_path(args.config).read_bytes())
    result = verify_deployment(config)
    sys.stdout.write(json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n")


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    category = error.args[0] if isinstance(error, DeploymentRefusalError) else "deployment-refused"
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": category, "realDataReady": False}) + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()
