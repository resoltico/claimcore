#!/usr/bin/env python3
"""Pinned remote CHECKPOINT command and trust-root refusal tests, no live SSH."""

import base64
import hashlib
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from checkpoint_signer_remote import REMOTE_COMMAND, request_remote
from deployment_common import DeploymentRefusal, canonical, sign, utc


def pair(root, name):
    private, public = root / (name + ".key"), root / (name + ".pub")
    for command in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        assert (
            subprocess.run(
                command,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            ).returncode
            == 0
        )
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def fixture(root):
    publication_private, publication_public = pair(root, "publication")
    _, checkpoint_public = pair(root, "checkpoint")
    host_private = root / "host-ssh"
    assert (
        subprocess.run(
            ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(host_private)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        ).returncode
        == 0
    )
    host_private.chmod(0o600)
    encoded = host_private.with_suffix(".pub").read_text("ascii").split()[1]
    key_sha = hashlib.sha256(base64.b64decode(encoded)).hexdigest()
    known = root / "known_hosts"
    known.write_text(
        "[checkpoint.example]:2222 ssh-ed25519 " + encoded + "\n", encoding="ascii"
    )
    known.chmod(0o600)
    now = datetime.now(timezone.utc).replace(microsecond=0)
    candidate = {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "checkpointSigningKeyId": str(uuid.uuid4()),
    }
    topology = {
        "format": "claimcore-checkpoint-signer-topology-1",
        **candidate,
        "purpose": "CHECKPOINT",
        "checkpointPublicKeySha256": hashlib.sha256(
            checkpoint_public.read_bytes()
        ).hexdigest(),
        "sshHost": "checkpoint.example",
        "sshUser": "checkpoint",
        "sshPort": 2222,
        "sshHostKeySha256": key_sha,
        "machineHash": "a" * 64,
        "storageHash": "b" * 64,
        "adminActorId": str(uuid.uuid4()),
        "issuedAt": utc(now),
        "validUntil": utc(now + timedelta(hours=1)),
    }
    signed = sign(topology, publication_private)
    source, signature = root / "topology.json", root / "topology.sig"
    source.write_bytes(canonical(topology))
    signature.write_bytes(base64.b64decode(signed["signatureBase64"]))
    source.chmod(0o600)
    signature.chmod(0o600)
    config = {
        "checkpointSignerMode": "REMOTE_SSH",
        "checkpointSignerTopologyFile": source,
        "checkpointSignerTopologySignatureFile": signature,
        "checkpointKnownHostsFile": known,
        "checkpointSshIdentityFile": host_private,
        "checkpointVerificationKey": checkpoint_public,
    }
    return publication_public, config, candidate, topology


class CheckpointRemoteTests(unittest.TestCase):
    def test_fixed_command_and_mutable_trust_refusals(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            public, config, candidate, _topology = fixture(root)
            payload = canonical({"synthetic": True})

            def runner(command, received):
                self.assertEqual(received, payload)
                self.assertEqual(command[-2:], [REMOTE_COMMAND, "--stdio-sign"])
                self.assertIn("/dev/null", command)
                self.assertIn("StrictHostKeyChecking=yes", command)
                self.assertIn("ProxyCommand=none", command)
                self.assertIn("ProxyJump=none", command)
                return canonical({"status": "synthetic-reply"})

            self.assertEqual(
                request_remote(
                    config, payload, candidate, root_key=public, runner=runner
                )["status"],
                "synthetic-reply",
            )
            with self.assertRaisesRegex(
                DeploymentRefusal, "publication-root-unavailable"
            ):
                request_remote(config, payload, candidate, runner=runner)
            with self.assertRaisesRegex(DeploymentRefusal, "checkpoint-remote-config"):
                request_remote(
                    {**config, "checkpointSignerCommand": "/tmp/stub"},
                    payload,
                    candidate,
                    root_key=public,
                    runner=runner,
                )
            with self.assertRaisesRegex(
                DeploymentRefusal, "checkpoint-topology-candidate"
            ):
                request_remote(
                    config,
                    payload,
                    {**candidate, "epoch": 2},
                    root_key=public,
                    runner=runner,
                )
            wrong_public = root / "wrong-checkpoint.pub"
            wrong_public.write_bytes(b"wrong public bytes")
            wrong_public.chmod(0o600)
            with self.assertRaisesRegex(DeploymentRefusal, "checkpoint-topology-key"):
                request_remote(
                    {**config, "checkpointVerificationKey": wrong_public},
                    payload,
                    candidate,
                    root_key=public,
                    runner=runner,
                )
            changed_known = root / "wrong-known_hosts"
            changed_known.write_text(
                "[checkpoint.example]:2222 ssh-ed25519 "
                + base64.b64encode(b"wrong").decode("ascii")
                + "\n",
                encoding="ascii",
            )
            changed_known.chmod(0o600)
            with self.assertRaisesRegex(DeploymentRefusal, "ssh-host-key-digest"):
                request_remote(
                    {**config, "checkpointKnownHostsFile": changed_known},
                    payload,
                    candidate,
                    root_key=public,
                    runner=runner,
                )


if __name__ == "__main__":
    unittest.main()
