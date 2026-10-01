"""Synthetic private source/signing positives and missing-object refusals."""

import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

sys.dont_write_bytecode = True
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_fixture import fixture, key, private
from backup_health_source_inspection import (
    inspect_archive,
    inspect_checkpoint,
    inspect_restore,
)
from deployment_common import DeploymentRefusalError, canonical

REPOSITORY = Path(__file__).parents[2]
SIGNATURE_BYTES = 64


def _run(script: str, *arguments: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [sys.executable, "-B", "eng/backup/" + script, *arguments],
        cwd=REPOSITORY,
        capture_output=True,
        check=False,
    )


class BackupHealthSourceTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name).resolve()
        self.root.chmod(0o700)
        self.policy, self.source, self.keys, self.archive = fixture(self.root)

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _prepare_source(self) -> Path:
        private(self.root, "policy.json", canonical(self.policy))
        private(self.root, "draft.json", canonical(self.source))
        prepared = self.root / "health.source.json"
        result = _run(
            "Prepare-BackupHealthSource.py",
            "--policy",
            str(self.root / "policy.json"),
            "--draft",
            str(self.root / "draft.json"),
            "--output",
            str(prepared),
        )
        self.assertEqual(result.returncode, 0)
        self.assertEqual(prepared.read_bytes(), canonical(self.source))
        return prepared

    def _sign_roles(self, prepared: Path) -> None:
        for role, key_path in self.keys.items():
            output = self.root / ("health." + role + ".sig")
            signed = _run(
                "Sign-BackupHealthRole.py",
                "--role",
                role,
                "--policy",
                str(self.root / "policy.json"),
                "--source",
                str(prepared),
                "--private-key",
                str(key_path),
                "--signature-output",
                str(output),
            )
            self.assertEqual(signed.returncode, 0)
            self.assertEqual(len(output.read_bytes()), SIGNATURE_BYTES)

    def _write_fence(self) -> None:
        fence = dict.fromkeys(
            (
                "handoffId",
                "w1Sequence",
                "w1Hash",
                "activationSequence",
                "activationHash",
                "oldGeneration",
                "newGeneration",
            )
        )
        fence["kind"] = "GENESIS"
        private(self.root, "fence.json", canonical(fence))

    def _certificate(self, prepared: Path) -> None:
        self._write_fence()
        issuer = key(self.root, "issuer")
        candidate_path = self.root / "health.certificate.json"
        prepared_candidate = _run(
            "Prepare-BackupHealthCertificate.py",
            "--policy",
            str(self.root / "policy.json"),
            "--source",
            str(prepared),
            "--writer-fence",
            str(self.root / "fence.json"),
            "--signer-key-id",
            str(uuid.uuid4()),
            "--signer-holder-actor-id",
            str(uuid.uuid4()),
            "--output",
            str(candidate_path),
        )
        self.assertEqual(prepared_candidate.returncode, 0)
        signed_candidate = _run(
            "Sign-BackupHealthCertificate.py",
            "--policy",
            str(self.root / "policy.json"),
            "--source",
            str(prepared),
            "--archive-signature",
            str(self.root / "health.archive.sig"),
            "--checkpoint-signature",
            str(self.root / "health.checkpoint.sig"),
            "--test-restore-signature",
            str(self.root / "health.test-restore.sig"),
            "--candidate",
            str(candidate_path),
            "--private-key",
            str(issuer),
            "--signature-output",
            str(self.root / "health.certificate.sig"),
        )
        self.assertEqual(signed_candidate.returncode, 0)
        self.assertEqual(len((self.root / "health.certificate.sig").read_bytes()), SIGNATURE_BYTES)

    def test_role_signing_requires_current_physical_files(self) -> None:
        self.assertEqual(parse_policy(canonical(self.policy))["policyId"], self.policy["policyId"])
        self.assertEqual(
            parse_source(canonical(self.source), self.policy)["cycleId"], self.source["cycleId"]
        )
        inspect_archive(self.source, self.policy)
        inspect_checkpoint(self.source, self.policy)
        inspect_restore(self.source, self.policy)
        prepared = self._prepare_source()
        self._sign_roles(prepared)
        self._certificate(prepared)
        first = self.source["objects"][0]
        (self.archive / first["relativePath"]).write_bytes(b"changed")
        with self.assertRaisesRegex(DeploymentRefusalError, "health-source-archive-object"):
            inspect_archive(self.source, self.policy)


if __name__ == "__main__":
    unittest.main()
