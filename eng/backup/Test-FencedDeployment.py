#!/usr/bin/env python3
"""Synthetic signed W1 deployment conjunction and adversarial recheck tests."""

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
from deployment_common import DeploymentRefusal, canonical, sign, utc
from deployment_fenced import fenced_documents, final_objects_digest
from deployment_product import product_fenced_recheck


def keypair(root, name):
    private, public = root / (name + ".key"), root / (name + ".pub")
    for args in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        result = subprocess.run(
            args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False
        )
        assert result.returncode == 0
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def private_bytes(root, name, data):
    path = root / name
    path.write_bytes(data)
    path.chmod(0o600)
    return path


def signed_files(root, name, document, key):
    envelope = sign(document, key)
    return (
        private_bytes(root, name + ".json", canonical(document)),
        private_bytes(
            root, name + ".sig", base64.b64decode(envelope["signatureBase64"])
        ),
    )


def objects():
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


def fixture(root):
    publication_key, publication_public = keypair(root, "publication")
    checkpoint_key, checkpoint_public = keypair(root, "checkpoint")
    archive = root / "archive"
    archive.mkdir(mode=0o700)
    now = datetime.now(timezone.utc).replace(microsecond=0)
    report_sha = "a" * 64
    report_signer, checkpoint_signer = str(uuid.uuid4()), str(uuid.uuid4())
    report = {
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
    fence = {
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
    fence_path, fence_sig = signed_files(root, "fence", fence, checkpoint_key)
    fence_sha = hashlib.sha256(canonical(fence)).hexdigest()
    tail = {
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
    tail_path, tail_sig = signed_files(root, "tail", tail, checkpoint_key)
    binary = private_bytes(root, "database.dll", b"synthetic fixed Database bytes")
    binary_sha = hashlib.sha256(binary.read_bytes()).hexdigest()
    publication = {
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
    publication_path, publication_sig = signed_files(
        root, "publication", publication, publication_key
    )
    config = {
        "fullReportPath": str(private_bytes(root, "report.json", b"synthetic report")),
        "fullReportSignaturePath": str(
            private_bytes(root, "report.sig", b"synthetic sig")
        ),
        "restoreEvidenceIndexPath": str(
            private_bytes(root, "index.json", b"synthetic index")
        ),
        "fenceReportPath": str(fence_path),
        "fenceReportSignaturePath": str(fence_sig),
        "fencedTailPath": str(tail_path),
        "fencedTailSignaturePath": str(tail_sig),
        "checkpointPublicKey": str(checkpoint_public),
        "restoreArchiveRoot": str(archive),
        "productVerifierSha256": binary_sha,
    }
    return (
        report,
        report_sha,
        fence,
        tail,
        config,
        (publication_path, publication_sig),
        publication_public,
        binary,
        checkpoint_key,
    )


class FencedDeploymentTests(unittest.TestCase):
    def test_cross_language_final_object_digest(self):
        self.assertEqual(
            final_objects_digest({"walObjects": objects()}),
            "b8d0600cad55a63b7e61cf779d95b56f7687abf50fd1172034b355e29358ee35",
        )

    def test_signed_product_conjunction_and_negatives(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            (
                report,
                report_sha,
                _,
                tail,
                config,
                publication,
                public,
                binary,
                checkpoint_key,
            ) = fixture(root)
            evidence = fenced_documents(config, report, report_sha)
            nonce = "e" * 64

            def response(command, supplied_nonce=nonce):
                self.assertEqual(command[2], "verify-fenced-tail")
                self.assertEqual(len(command), 11)
                return canonical(
                    {
                        "format": "claimcore-fenced-tail-recheck-1",
                        "nonce": supplied_nonce,
                        "status": "EVIDENCE_RECHECKED",
                        "reportSha256": report_sha,
                        "fenceReportSha256": evidence["fenceSha256"],
                        "supplementSha256": evidence["tailSha256"],
                        "finalWalObjectSha256": final_objects_digest(tail),
                        "w1Sequence": 11,
                        "w1Hash": "d" * 64,
                        "writerGeneration": 2,
                        "realDataReady": False,
                    }
                )

            args = {
                "root_key": public,
                "publication_files": publication,
                "product_runner": response,
                "binary_path": binary,
            }
            result = product_fenced_recheck(
                config, nonce, report_sha, report, evidence, **args
            )
            self.assertTrue(result["syntheticOnly"])
            for changed, reason in (
                (
                    {**config, "productVerifierPath": str(binary)},
                    "config-selected-product-verifier",
                ),
                (
                    {**config, "publicationRootPath": str(public)},
                    "config-selected-product-verifier",
                ),
            ):
                with self.assertRaisesRegex(DeploymentRefusal, reason):
                    product_fenced_recheck(
                        changed, nonce, report_sha, report, evidence, **args
                    )
            with self.assertRaisesRegex(DeploymentRefusal, "product-fenced-mismatch"):
                product_fenced_recheck(
                    config,
                    nonce,
                    report_sha,
                    report,
                    evidence,
                    **{
                        **args,
                        "product_runner": lambda command: response(command, "0" * 64),
                    },
                )
            changed_tail = {**tail, "reportSha256": "f" * 64}
            path, signature = signed_files(
                root, "changed-tail", changed_tail, checkpoint_key
            )
            with self.assertRaisesRegex(
                DeploymentRefusal, "fenced-tail-link|fenced-tail-w1"
            ):
                fenced_documents(
                    {
                        **config,
                        "fencedTailPath": str(path),
                        "fencedTailSignaturePath": str(signature),
                    },
                    report,
                    report_sha,
                )
            binary.write_bytes(b"altered fixed Database bytes")
            with self.assertRaisesRegex(DeploymentRefusal, "published-verifier-digest"):
                product_fenced_recheck(
                    config, nonce, report_sha, report, evidence, **args
                )


if __name__ == "__main__":
    unittest.main()
