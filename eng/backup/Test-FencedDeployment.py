#!/usr/bin/env python3
"""Synthetic signed W1 deployment conjunction and adversarial recheck tests."""

import hashlib
import sys
import tempfile
import unittest
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

sys.dont_write_bytecode = True
from backup_test_support import keypair, private_bytes, signed_files
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, canonical, utc
from deployment_fenced import fenced_documents
from deployment_fenced_objects import final_objects_digest
from deployment_product import ProductOverrides, product_fenced_recheck


def objects() -> list[JsonObject]:
    return [
        {
            "objectId": f"00000000-0000-0000-0000-00000000000{index}",
            "cluster": cluster,
            "relativePath": f"{label}-tail/000000010000000000000001.age",
            "ciphertextSha256": digest * 64,
            "ciphertextBytes": 16777316,
            "walSegment": "000000010000000000000001",
            "walSegmentBytes": 16777216,
        }
        for index, cluster, label, digest in (
            (1, "PRIMARY", "primary", "a"),
            (2, "WITNESS", "witness", "b"),
        )
    ]


@dataclass(frozen=True)
class Fixture:
    """Synthetic signed evidence plus the files and keys that produced it."""

    root: Path
    report: JsonObject
    report_sha: str
    tail: JsonObject
    config: JsonObject
    publication: tuple[Path, Path]
    public: Path
    binary: Path
    checkpoint_key: Path


def report_document(report_signer: str) -> JsonObject:
    return {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "witnessCutoff": 10,
        "witnessCutoffHash": "b" * 64,
        "primaryTimeline": 1,
        "witnessTimeline": 1,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "reportSignerKeyId": report_signer,
    }


def fence_document(
    report: JsonObject, report_sha: str, checkpoint_signer: str, now: datetime
) -> JsonObject:
    return {
        "format": "claimcore-old-writer-isolation-1",
        "installationId": report["installationId"],
        "lineageId": report["lineageId"],
        "epoch": 1,
        "oldGeneration": 1,
        "newGeneration": 2,
        "reportSha256": report_sha,
        "independentProbeSha256": "c" * 64,
        "checkpointSignerKeyId": checkpoint_signer,
        "oldEndpointId": str(uuid.uuid4()),
        "oldEndpointAddressSha256": "1" * 64,
        "oldPrimaryRoleOid": 101,
        "oldWitnessRoleOid": 102,
        "oldPrimaryCredentialSha256": "2" * 64,
        "oldWitnessCredentialSha256": "3" * 64,
        "primarySessionSetSha256": "4" * 64,
        "witnessSessionSetSha256": "5" * 64,
        "oldWriterStopped": True,
        "primarySessionsTerminated": True,
        "witnessSessionsTerminated": True,
        "oldEndpointIsolated": True,
        "primaryCredentialRevoked": True,
        "witnessCredentialRevoked": True,
        "preIsolationCommitReconciled": True,
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=10)),
    }


def tail_document(
    report: JsonObject, links: tuple[str, str, str], archive: Path, now: datetime
) -> JsonObject:
    report_sha, fence_sha, checkpoint_signer = links
    return {
        "format": "claimcore-fenced-recovery-tail-1",
        "scope": "full",
        "realDataReady": False,
        "recoveryTailSealed": True,
        "installationId": report["installationId"],
        "lineageId": report["lineageId"],
        "epoch": 1,
        "oldGeneration": 1,
        "newGeneration": 2,
        "handoffId": str(uuid.uuid4()),
        "w1Sequence": 11,
        "w1Hash": "d" * 64,
        "reportSha256": report_sha,
        "fenceReportSha256": fence_sha,
        "checkpointSignerKeyId": checkpoint_signer,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "primaryFinalWalEndpoint": "0/200",
        "witnessFinalWalEndpoint": "0/200",
        "archiveRoot": str(archive),
        "walObjects": objects(),
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=10)),
    }


def publication_document(
    report: JsonObject, binary_sha: str, signers: tuple[str, str], now: datetime
) -> JsonObject:
    report_signer, checkpoint_signer = signers
    return {
        "format": "claimcore-publication-manifest-1",
        "publicationId": str(uuid.uuid4()),
        "verifierBinarySha256": binary_sha,
        "reportSignerKeyId": report_signer,
        "checkpointSignerKeyId": checkpoint_signer,
        "installationId": report["installationId"],
        "lineageId": report["lineageId"],
        "epoch": 1,
        "writerGeneration": 2,
        "witnessCutoff": 11,
        "witnessCutoffHash": "d" * 64,
        "issuedAt": utc(now),
        "validUntil": utc(now + timedelta(hours=1)),
    }


def fixture(root: Path) -> Fixture:
    publication_key, publication_public = keypair(root, "publication")
    checkpoint_key, checkpoint_public = keypair(root, "checkpoint")
    archive = root / "archive"
    archive.mkdir(mode=0o700)
    now = datetime.now(UTC).replace(microsecond=0)
    report_sha = "a" * 64
    report_signer, checkpoint_signer = str(uuid.uuid4()), str(uuid.uuid4())
    report = report_document(report_signer)
    fence = fence_document(report, report_sha, checkpoint_signer, now)
    fence_path, fence_sig = signed_files(root, "fence", fence, checkpoint_key)
    fence_sha = hashlib.sha256(canonical(fence)).hexdigest()
    tail = tail_document(report, (report_sha, fence_sha, checkpoint_signer), archive, now)
    tail_path, tail_sig = signed_files(root, "tail", tail, checkpoint_key)
    binary = private_bytes(root, "database.dll", b"synthetic fixed Database bytes")
    binary_sha = hashlib.sha256(binary.read_bytes()).hexdigest()
    publication = publication_document(report, binary_sha, (report_signer, checkpoint_signer), now)
    config = {
        "fullReportPath": str(private_bytes(root, "report.json", b"synthetic report")),
        "fullReportSignaturePath": str(private_bytes(root, "report.sig", b"synthetic sig")),
        "restoreEvidenceIndexPath": str(private_bytes(root, "index.json", b"synthetic index")),
        "fenceReportPath": str(fence_path),
        "fenceReportSignaturePath": str(fence_sig),
        "fencedTailPath": str(tail_path),
        "fencedTailSignaturePath": str(tail_sig),
        "checkpointPublicKey": str(checkpoint_public),
        "restoreArchiveRoot": str(archive),
        "productVerifierSha256": binary_sha,
    }
    return Fixture(
        root,
        report,
        report_sha,
        tail,
        config,
        signed_files(root, "publication", publication, publication_key),
        publication_public,
        binary,
        checkpoint_key,
    )


NONCE = "e" * 64


class FencedDeploymentTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        root = Path(self._directory.name).resolve()
        root.chmod(0o700)
        self.fixture = fixture(root)
        self.evidence = fenced_documents(
            self.fixture.config, self.fixture.report, self.fixture.report_sha
        )

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _response(self, command: Sequence[str], supplied_nonce: str = NONCE) -> bytes:
        self.assertEqual(command[2], "verify-fenced-tail")
        self.assertEqual(len(command), 11)
        return canonical(
            {
                "format": "claimcore-fenced-tail-recheck-1",
                "nonce": supplied_nonce,
                "status": "EVIDENCE_RECHECKED",
                "reportSha256": self.fixture.report_sha,
                "fenceReportSha256": self.evidence["fenceSha256"],
                "supplementSha256": self.evidence["tailSha256"],
                "finalWalObjectSha256": final_objects_digest(self.fixture.tail),
                "w1Sequence": 11,
                "w1Hash": "d" * 64,
                "writerGeneration": 2,
                "realDataReady": False,
            }
        )

    def _recheck(
        self,
        *,
        config: JsonObject | None = None,
        runner: Callable[[Sequence[str]], bytes] | None = None,
    ) -> JsonObject:
        overrides = ProductOverrides(
            root_key=self.fixture.public,
            publication_files=self.fixture.publication,
            product_runner=runner or self._response,
            binary_path=self.fixture.binary,
        )
        return product_fenced_recheck(
            config or self.fixture.config,
            NONCE,
            self.fixture.report_sha,
            self.fixture.report,
            self.evidence,
            overrides=overrides,
        )

    def test_cross_language_final_object_digest(self) -> None:
        self.assertEqual(
            final_objects_digest({"walObjects": objects()}),
            "b8d0600cad55a63b7e61cf779d95b56f7687abf50fd1172034b355e29358ee35",
        )

    def test_signed_product_conjunction(self) -> None:
        self.assertTrue(self._recheck()["syntheticOnly"])

    def test_config_cannot_select_product_inputs(self) -> None:
        for key, value in (
            ("productVerifierPath", self.fixture.binary),
            ("publicationRootPath", self.fixture.public),
        ):
            with self.assertRaisesRegex(DeploymentRefusalError, "config-selected-product-verifier"):
                self._recheck(config={**self.fixture.config, key: str(value)})

    def test_wrong_nonce_is_a_mismatch(self) -> None:
        with self.assertRaisesRegex(DeploymentRefusalError, "product-fenced-mismatch"):
            self._recheck(runner=lambda command: self._response(command, "0" * 64))

    def test_tail_linked_to_a_different_report_is_refused(self) -> None:
        changed_tail = {**self.fixture.tail, "reportSha256": "f" * 64}
        path, signature = signed_files(
            self.fixture.root, "changed-tail", changed_tail, self.fixture.checkpoint_key
        )
        changed = {
            **self.fixture.config,
            "fencedTailPath": str(path),
            "fencedTailSignaturePath": str(signature),
        }
        with self.assertRaisesRegex(DeploymentRefusalError, "fenced-tail-link|fenced-tail-w1"):
            fenced_documents(changed, self.fixture.report, self.fixture.report_sha)

    def test_altered_published_verifier_is_refused(self) -> None:
        self.fixture.binary.write_bytes(b"altered fixed Database bytes")
        with self.assertRaisesRegex(DeploymentRefusalError, "published-verifier-digest"):
            self._recheck()


if __name__ == "__main__":
    unittest.main()
