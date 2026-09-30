"""Local probe, SSH pin and private-path negatives for deployment admission."""

import base64
import hashlib
import subprocess
import uuid
from pathlib import Path

from backup_test_support import ensure, keypair, refuses
from backup_types import JsonObject
from deployment_admission_fixture import Deployment, final_wal_objects
from deployment_common import private_path, verify
from deployment_probe import probe
from deployment_ssh import ssh_command

NONCE, REPORT_SHA = "a" * 64, "b" * 64
AMBIGUOUS_HOST_KEY = "ssh-host-key-not-uniquely-pinned"
AMBIGUOUS_KNOWN_HOSTS = "ssh-known-hosts-ambiguous"


def _write_final_wal_objects(root: Path) -> list[JsonObject]:
    objects = final_wal_objects()
    for index, item in enumerate(objects, 1):
        destination = root / item["relativePath"]
        destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        destination.parent.chmod(0o700)
        destination.write_bytes(bytes([index]) * item["ciphertextBytes"])
        destination.chmod(0o600)
        item["ciphertextSha256"] = hashlib.sha256(destination.read_bytes()).hexdigest()
    return objects


def local_probe_negative(root: Path) -> None:
    """Verify a local archive probe, which answers only for the evidence it holds."""
    private, public = keypair(root, "local-probe")
    marker = root / "synthetic-copy.age"
    marker.write_bytes(b"synthetic encrypted-copy marker")
    marker.chmod(0o600)
    config: JsonObject = {
        "format": "claimcore-deployment-probe-config-1",
        "role": "archive",
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        "storagePath": str(root),
        "availabilityFile": str(marker),
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "objectId": str(uuid.uuid4()),
        "objectSha256": hashlib.sha256(marker.read_bytes()).hexdigest(),
        "objectBytes": marker.stat().st_size,
        "finalWalArchiveRoot": str(root),
        "finalWalObjects": _write_final_wal_objects(root),
        "probeSigningKey": str(private),
    }
    verified, _ = verify(probe(config, "archive", NONCE, REPORT_SHA, None), public)
    ensure(verified["role"] == "archive" and verified["available"] is True, "local-probe-report")


def _generate_ssh_identity(root: Path) -> tuple[Path, list[str]]:
    identity = root / "ssh-probe-identity"
    generated = subprocess.run(
        ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(identity)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    ensure(generated.returncode == 0, "ssh-keygen-failed")
    identity.chmod(0o600)
    return identity, identity.with_suffix(".pub").read_text("ascii").split()


def _pin_primary(
    config: JsonObject, known: Path, identity: Path, key_words: list[str]
) -> JsonObject:
    pinned: JsonObject = config["roles"]["primary"]
    pinned.update(
        {
            "sshHost": "synthetic.example",
            "sshUser": "claimcore",
            "sshPort": 22,
            "remoteProbePath": "/opt/claimcore/deployment_probe.py",
            "knownHostsFile": str(known),
            "sshIdentityFile": str(identity),
            "sshHostKeySha256": hashlib.sha256(base64.b64decode(key_words[1])).hexdigest(),
        }
    )
    return pinned


def ssh_pin_negative(root: Path, deployment: Deployment) -> None:
    """Require one exact known-hosts pin and a matching digest for the fixed SSH command."""
    config = deployment.config
    identity, key_words = _generate_ssh_identity(root)
    known = root / "known_hosts"
    known.write_text("synthetic.example " + " ".join(key_words[:2]) + "\n", encoding="ascii")
    known.chmod(0o600)
    pinned = _pin_primary(config, known, identity, key_words)

    def command() -> list[str]:
        return ssh_command(config, "primary", NONCE, REPORT_SHA, "")

    built = command()
    ensure("StrictHostKeyChecking=yes" in built, "ssh-strict-host-key-checking")
    ensure("ProxyCommand=none" in built and "/dev/null" in built, "ssh-no-proxy")
    original = known.read_text("ascii")
    key = " ".join(key_words[:2])
    for content, category in (
        ("", AMBIGUOUS_HOST_KEY),
        (original + original, AMBIGUOUS_HOST_KEY),
        ("*.example " + key + "\n", AMBIGUOUS_KNOWN_HOSTS),
        ("@cert-authority synthetic.example " + key + "\n", AMBIGUOUS_KNOWN_HOSTS),
    ):
        known.write_text(content, encoding="ascii")
        refuses(command, category)
    known.write_text(original, encoding="ascii")
    pinned["sshHostKeySha256"] = "0" * 64
    refuses(command, "ssh-host-key-digest")


def private_path_negative(root: Path) -> None:
    """Require private paths to sit under owner-only directories and never be links."""
    public_parent = root / "public-parent"
    public_parent.mkdir(mode=0o755)
    content = public_parent / "private.json"
    content.write_text("{}", encoding="ascii")
    content.chmod(0o600)
    refuses(lambda: private_path(content), "private-path-permissions")
    linked = root / "linked.json"
    linked.symlink_to(content)
    refuses(lambda: private_path(linked), "linked-private-path")
