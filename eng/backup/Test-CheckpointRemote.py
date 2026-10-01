#!/usr/bin/env python3
"""Pinned remote CHECKPOINT command and trust-root refusal tests, no live SSH."""

import base64
import hashlib
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

sys.dont_write_bytecode = True
from backup_types import JsonObject
from checkpoint_signer_remote import REMOTE_COMMAND, request_remote
from deployment_common import DeploymentRefusalError, canonical, sign, utc


def run_quietly(command: list[str]) -> None:
    result = subprocess.run(
        command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False
    )
    assert result.returncode == 0


def pair(root: Path, name: str) -> tuple[Path, Path]:
    private, public = root / (name + ".key"), root / (name + ".pub")
    run_quietly(["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)])
    run_quietly(["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)])
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def host_identity(root: Path) -> tuple[Path, str]:
    """Return a synthetic SSH identity file and the SHA-256 of its public host key."""
    host_private = root / "host-ssh"
    run_quietly(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(host_private)])
    host_private.chmod(0o600)
    encoded = host_private.with_suffix(".pub").read_text("ascii").split()[1]
    known = root / "known_hosts"
    known.write_text("[checkpoint.example]:2222 ssh-ed25519 " + encoded + "\n", encoding="ascii")
    known.chmod(0o600)
    return host_private, hashlib.sha256(base64.b64decode(encoded)).hexdigest()


def signed_topology(
    root: Path,
    candidate: JsonObject,
    keys: tuple[Path, Path],
    host_key_sha: str,
) -> tuple[Path, Path]:
    publication_private, checkpoint_public = keys
    now = datetime.now(UTC).replace(microsecond=0)
    topology = {
        "format": "claimcore-checkpoint-signer-topology-1",
        **candidate,
        "purpose": "CHECKPOINT",
        "checkpointPublicKeySha256": hashlib.sha256(checkpoint_public.read_bytes()).hexdigest(),
        "sshHost": "checkpoint.example",
        "sshUser": "checkpoint",
        "sshPort": 2222,
        "sshHostKeySha256": host_key_sha,
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
    return source, signature


def fixture(root: Path) -> tuple[Path, JsonObject, JsonObject]:
    publication_private, publication_public = pair(root, "publication")
    _, checkpoint_public = pair(root, "checkpoint")
    host_private, host_key_sha = host_identity(root)
    candidate: JsonObject = {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "checkpointSigningKeyId": str(uuid.uuid4()),
    }
    source, signature = signed_topology(
        root, candidate, (publication_private, checkpoint_public), host_key_sha
    )
    config: JsonObject = {
        "checkpointSignerMode": "REMOTE_SSH",
        "checkpointSignerTopologyFile": source,
        "checkpointSignerTopologySignatureFile": signature,
        "checkpointKnownHostsFile": root / "known_hosts",
        "checkpointSshIdentityFile": host_private,
        "checkpointVerificationKey": checkpoint_public,
    }
    return publication_public, config, candidate


class CheckpointRemoteTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name).resolve()
        self.root.chmod(0o700)
        self.public, self.config, self.candidate = fixture(self.root)
        self.payload = canonical({"synthetic": True})

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _runner(self, command: list[str], received: bytes) -> bytes:
        self.assertEqual(received, self.payload)
        self.assertEqual(command[-2:], [REMOTE_COMMAND, "--stdio-sign"])
        self.assertIn("/dev/null", command)
        self.assertIn("StrictHostKeyChecking=yes", command)
        self.assertIn("ProxyCommand=none", command)
        self.assertIn("ProxyJump=none", command)
        return canonical({"status": "synthetic-reply"})

    def _request(self, **changes: JsonObject) -> JsonObject:
        return request_remote(
            {**self.config, **changes.get("config", {})},
            self.payload,
            {**self.candidate, **changes.get("candidate", {})},
            root_key=self.public,
            runner=self._runner,
        )

    def test_fixed_command_is_the_only_invocation(self) -> None:
        self.assertEqual(self._request()["status"], "synthetic-reply")

    def test_root_and_candidate_refusals(self) -> None:
        with self.assertRaisesRegex(DeploymentRefusalError, "publication-root-unavailable"):
            request_remote(self.config, self.payload, self.candidate, runner=self._runner)
        with self.assertRaisesRegex(DeploymentRefusalError, "checkpoint-remote-config"):
            self._request(config={"checkpointSignerCommand": str(self.root / "stub")})
        with self.assertRaisesRegex(DeploymentRefusalError, "checkpoint-topology-candidate"):
            self._request(candidate={"epoch": 2})

    def test_mutable_key_and_host_trust_refusals(self) -> None:
        wrong_public = self.root / "wrong-checkpoint.pub"
        wrong_public.write_bytes(b"wrong public bytes")
        wrong_public.chmod(0o600)
        with self.assertRaisesRegex(DeploymentRefusalError, "checkpoint-topology-key"):
            self._request(config={"checkpointVerificationKey": wrong_public})
        changed_known = self.root / "wrong-known_hosts"
        changed_known.write_text(
            "[checkpoint.example]:2222 ssh-ed25519 "
            + base64.b64encode(b"wrong").decode("ascii")
            + "\n",
            encoding="ascii",
        )
        changed_known.chmod(0o600)
        with self.assertRaisesRegex(DeploymentRefusalError, "ssh-host-key-digest"):
            self._request(config={"checkpointKnownHostsFile": changed_known})


if __name__ == "__main__":
    unittest.main()
