"""Backup configuration must be usable by the native custody and identity readers."""

import hashlib
import os
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

from backup_test_support import keypair, private_bytes
from backup_types import JsonObject
from deployment_common import canonical
from managed_common import BackupFailureError
from managed_config import configuration

POLICY = {
    "backupIntervalSeconds": 60,
    "maximumBackupAgeSeconds": 120,
    "restoreHorizonSeconds": 180,
    "backupRetentionSeconds": 86400,
    "walRetentionSeconds": 86400,
    "checkpointRetentionSeconds": 172800,
    "maxBackupBytes": 1048576,
    "maxTarEntries": 100,
    "maxWalCopies": 100,
}


def service_file(root: Path) -> Path:
    sections = []
    for cluster, port in (("primary", 5432), ("witness", 5433)):
        for kind in ("metadata", "replication"):
            body = (
                f"[{cluster}_{kind}]\nhost=127.0.0.1\nport={port}\n"
                f"user=synthetic_{kind}\npassword=synthetic-only\n"
            )
            if kind == "metadata":
                body += f"dbname={cluster}_test\n"
            sections.append(body)
    return private_bytes(root, "services.conf", "\n".join(sections).encode())


def settings(root: Path, services: Path) -> JsonObject:
    for name in ("archive", "checkpoints", "inventory"):
        (root / name).mkdir(mode=0o700)
    private, public = keypair(root, "attestor")
    _, checkpoint = keypair(root, "checkpoint")
    key = private_bytes(root, "commitment.key", bytes(range(1, 33)))
    return {
        "format": "claimcore-managed-backup-1",
        **POLICY,
        "archiveRoot": str(root / "archive"),
        "checkpointRoot": str(root / "checkpoints"),
        "inventoryRoot": str(root / "inventory"),
        "commitmentKey": str(key),
        "signingKeyId": str(uuid.uuid4()),
        "checkpointSigningKeyId": str(uuid.uuid4()),
        "encryptionKeyId": str(uuid.uuid4()),
        "ageRecipient": "age1" + "q" * 58,
        "signingKey": str(private),
        "verificationKey": str(public),
        "checkpointVerificationKey": str(checkpoint),
        "checkpointSignerMode": "LOCAL_SYNTHETIC",
        "checkpointSignerRemote": None,
        "checkpointSignerSocket": str(root / "signer.sock"),
        "checkpointCustodianId": "checkpoint-custodian",
        "pgServiceFile": str(services),
        "pgServiceFileSha256": hashlib.sha256(services.read_bytes()).hexdigest(),
        "pgTlsRootSha256": None,
        "primary": {
            "metadataService": "primary_metadata",
            "replicationService": "primary_replication",
            "custodianId": "primary-custodian",
        },
        "witness": {
            "metadataService": "witness_metadata",
            "replicationService": "witness_replication",
            "custodianId": "witness-custodian",
        },
    }


class ManagedConfigurationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name).resolve()
        self.root.chmod(0o700)
        services = service_file(self.root)
        self.config = settings(self.root, services)
        self.path = private_bytes(self.root, "config.json", canonical(self.config))
        self.environment = patch.dict(os.environ, {"PGSERVICEFILE": str(services)})
        self.environment.start()

    def tearDown(self) -> None:
        self.environment.stop()
        self.directory.cleanup()

    def test_native_compatible_configuration(self) -> None:
        loaded = configuration(self.path)
        self.assertEqual(loaded["signingKeyId"], self.config["signingKeyId"])

    def test_commitment_material_matches_native_custody(self) -> None:
        key = Path(self.config["commitmentKey"])
        for material, cause in (
            (bytes(31), "commitment-key-size"),
            (bytes(range(64)), "commitment-key-size"),
            (bytes(32), "commitment-key-material"),
        ):
            key.write_bytes(material)
            with self.assertRaisesRegex(BackupFailureError, cause):
                configuration(self.path)

    def test_key_identity_is_canonical_and_non_nil(self) -> None:
        for name in ("signingKeyId", "checkpointSigningKeyId", "encryptionKeyId"):
            for identity in (str(uuid.UUID(int=0)), "A0000000-0000-4000-8000-000000000001"):
                changed = {**self.config, name: identity}
                self.path.write_bytes(canonical(changed))
                with self.assertRaisesRegex(BackupFailureError, "backup-key-identity"):
                    configuration(self.path)

    def test_distinct_pem_files_cannot_reuse_one_signer_key(self) -> None:
        lines = Path(self.config["verificationKey"]).read_text().splitlines()
        encoded = "".join(lines[1:-1])
        wrapped = "\n".join(encoded[i : i + 20] for i in range(0, len(encoded), 20))
        alias = private_bytes(
            self.root, "alias.pub", (lines[0] + "\n" + wrapped + "\n" + lines[-1] + "\n").encode()
        )
        self.path.write_bytes(canonical({**self.config, "checkpointVerificationKey": str(alias)}))
        with self.assertRaisesRegex(BackupFailureError, "checkpoint-signer-separation"):
            configuration(self.path)


if __name__ == "__main__":
    unittest.main()
