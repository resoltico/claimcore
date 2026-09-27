#!/usr/bin/env python3
"""Owner-only deploy-time independent-host gate; no cloud dependency."""

import argparse
import base64
import hashlib
import json
import os
import re
import secrets
import subprocess
import sys
import uuid
from pathlib import Path

sys.dont_write_bytecode = True
from deployment_aggregate import make_aggregate
from deployment_aggregate_io import write_aggregate
from deployment_common import DeploymentRefusal, private_path, require
from deployment_fenced import fenced_documents
from deployment_observer import verify_observer
from deployment_product import (
    REVIEWED_PUBLICATION_ROOT_PEM,
    bounded,
    product_fenced_recheck,
)
from deployment_report import pre_handoff_report
from deployment_topology import verify_topology
from deployment_trust import ROLES, evaluate


def _text(value, pattern, category):
    require(
        isinstance(value, str) and re.fullmatch(pattern, value) is not None, category
    )
    return value


def _pinned_host_key(known, lookup, expected):
    _text(expected, r"[0-9a-f]{64}", "ssh-host-key-digest")
    matches = []
    try:
        lines = known.read_text("ascii").splitlines()
    except (OSError, UnicodeError):
        raise DeploymentRefusal("ssh-known-hosts-invalid") from None
    require(len(lines) <= 1024, "ssh-known-hosts-invalid")
    for line in lines:
        if not line or line.startswith("#"):
            continue
        words = line.split()
        require(len(words) in (3, 4), "ssh-known-hosts-invalid")
        require(not words[0].startswith("@"), "ssh-known-hosts-ambiguous")
        host_pattern, algorithm, encoded = words[:3]
        require(
            not any(mark in host_pattern for mark in ("*", "?", "!", "|", ",")),
            "ssh-known-hosts-ambiguous",
        )
        if host_pattern == lookup:
            require(algorithm == "ssh-ed25519", "ssh-host-key-algorithm")
            try:
                key_bytes = base64.b64decode(encoded, validate=True)
            except (ValueError, base64.binascii.Error):
                raise DeploymentRefusal("ssh-known-hosts-invalid") from None
            require(len(key_bytes) <= 2048, "ssh-known-hosts-invalid")
            matches.append(hashlib.sha256(key_bytes).hexdigest())
    require(len(matches) == 1, "ssh-host-key-not-uniquely-pinned")
    require(matches[0] == expected, "ssh-host-key-digest")


def ssh_command(
    config, role, nonce, qualification_sha, challenge, *, pinned=None, extra=()
):
    pinned = config["roles"][role] if pinned is None else pinned
    host = _text(pinned["sshHost"], r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", "ssh-host")
    user = _text(pinned["sshUser"], r"[A-Za-z_][A-Za-z0-9_-]{0,31}", "ssh-user")
    remote = _text(
        pinned["remoteProbePath"], r"/[A-Za-z0-9_./-]{1,255}", "ssh-probe-path"
    )
    require(".." not in Path(remote).parts, "ssh-probe-path")
    port = pinned["sshPort"]
    require(type(port) is int and 1 <= port <= 65535, "ssh-port")
    known = private_path(pinned["knownHostsFile"])
    identity = private_path(pinned["sshIdentityFile"])
    private_path(pinned["probePublicKey"])
    lookup = host if port == 22 else f"[{host}]:{port}"
    _pinned_host_key(known, lookup, pinned["sshHostKeySha256"])
    known_result = subprocess.run(
        ["ssh-keygen", "-F", lookup, "-f", str(known)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(known_result.returncode == 0, "ssh-host-key-not-pinned")
    command = [
        "ssh",
        "-F",
        "/dev/null",
        "-i",
        str(identity),
        "-o",
        "IdentitiesOnly=yes",
        "-o",
        "BatchMode=yes",
        "-o",
        "StrictHostKeyChecking=yes",
        "-o",
        f"UserKnownHostsFile={known}",
        "-o",
        "GlobalKnownHostsFile=/dev/null",
        "-o",
        "ProxyCommand=none",
        "-o",
        "ProxyJump=none",
        "-o",
        "ConnectTimeout=10",
        "-p",
        str(port),
        f"{user}@{host}",
        remote,
        "--role",
        role,
        "--nonce",
        nonce,
        "--qualification-sha256",
        qualification_sha,
    ]
    if role == "key":
        command.extend(["--key-challenge", challenge])
    command.extend(extra)
    return command


def key_challenge(config):
    recipient = _text(
        config["roles"]["key"]["ageRecipient"],
        r"age1[023456789acdefghjklmnpqrstuvwxyz]{58}",
        "key-recipient",
    )
    secret = secrets.token_bytes(32)
    result = subprocess.run(
        ["age", "--encrypt", "--recipient", recipient],
        input=secret,
        capture_output=True,
        timeout=10,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= 4096,
        "key-challenge-encryption",
    )
    return base64.b64encode(result.stdout).decode("ascii"), hashlib.sha256(
        secret
    ).hexdigest()


def verify_deployment(config):
    require(
        config.get("format") == "claimcore-deployment-1", "deployment-config-format"
    )
    require(
        not any(
            name in config
            for name in (
                "productVerifierPath",
                "productRecheckCommand",
                "publicationRootPath",
            )
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
    qualification, qualification_sha = pre_handoff_report(config)
    nonce = secrets.token_hex(32)
    fenced = fenced_documents(config, qualification, qualification_sha)
    product = product_fenced_recheck(
        config, nonce, qualification_sha, qualification, fenced
    )
    require(not product["syntheticOnly"], "production-evidence-unavailable")
    topology, topology_sha = verify_topology(
        REVIEWED_PUBLICATION_ROOT_PEM,
        config["topologyManifestPath"],
        config["topologySignaturePath"],
        product["publicationSha256"],
        config,
    )
    challenge, key_proof = key_challenge(config)
    tail = fenced["tail"]
    require(
        topology["installationId"] == tail["installationId"]
        and topology["lineageId"] == tail["lineageId"]
        and topology["epoch"] == tail["epoch"]
        and topology["writerGeneration"] == tail["newGeneration"]
        and topology["w1Sequence"] == tail["w1Sequence"]
        and topology["w1Hash"] == tail["w1Hash"],
        "topology-w1-link",
    )
    probe_qualification = {
        **qualification,
        "witnessCutoff": tail["w1Sequence"],
        "witnessCutoffHash": tail["w1Hash"],
        "finalWalObjects": tail["walObjects"],
        "finalWalObjectSha256": fenced["finalWalObjectSha256"],
    }
    envelopes = {}
    for role in ROLES:
        command = ssh_command(config, role, nonce, fenced["tailSha256"], challenge)
        response = bounded(command, 16384, 30)
        envelopes[role] = json.loads(response)
    result = evaluate(
        config,
        envelopes,
        nonce,
        fenced["tailSha256"],
        key_proof,
        probe_qualification,
        live_transport=True,
    )
    observer_command = ssh_command(
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
    old_observer = json.loads(bounded(observer_command, 16384, 30))
    _, observation_sha = verify_observer(
        old_observer,
        config["fenceObserver"]["probePublicKey"],
        topology["fenceObserverPin"],
        nonce,
        qualification_sha,
        fenced["fenceSha256"],
        tail,
        fenced["fence"],
    )
    aggregate_key = os.environ.get("CLAIMCORE_DEPLOYMENT_AGGREGATE_SIGNING_KEY_FILE")
    require(aggregate_key is not None, "aggregate-signing-key-unavailable")
    raw, signature = make_aggregate(
        product["publicationSha256"],
        topology_sha,
        qualification_sha,
        fenced,
        nonce,
        envelopes,
        old_observer,
        topology,
        {role: config["roles"][role]["probePublicKey"] for role in ROLES},
        config["fenceObserver"]["probePublicKey"],
        aggregate_key,
        config["aggregatePublicKeyFile"],
        config["productVerifierSha256"],
        scope="full",
    )
    write_aggregate(config, raw, signature)
    result["productRechecked"] = True
    result["publicationSha256"] = product["publicationSha256"]
    result["topologySha256"] = topology_sha
    result["oldWriterFenceObservationSha256"] = observation_sha
    result["aggregateSha256"] = hashlib.sha256(raw).hexdigest()
    return result


def main():
    os.umask(0o077)
    require(sys.version_info >= (3, 12), "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    config = json.loads(private_path(args.config).read_bytes())
    result = verify_deployment(config)
    sys.stdout.write(json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n")


def safe_error(_kind, error, _traceback):
    category = (
        error.args[0] if isinstance(error, DeploymentRefusal) else "deployment-refused"
    )
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": category, "realDataReady": False})
        + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()
