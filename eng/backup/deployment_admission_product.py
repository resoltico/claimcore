"""Forged-report and product-recheck negatives for deployment admission."""

import base64
import hashlib
import uuid
from collections.abc import Sequence
from dataclasses import dataclass, replace
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_test_support import ensure, keypair, private_bytes, refuses, signed_files
from backup_types import JsonObject
from deployment_admission_fixture import Deployment, qualification
from deployment_common import canonical, sign, utc
from deployment_product import ProductOverrides, product_recheck
from deployment_verify import verify_deployment

EXPIRED = "2000-01-01T00:00:00Z"
VERIFIED_FLAGS = (
    "catalogVerified",
    "dataAuditVerified",
    "registeredWalVerified",
    "recoveryTailUnsealed",
    "authorityReconciled",
    "managedCopiesRegistered",
    "newerFencesApplied",
    "quiescentAuditBarrierVerified",
    "oidcIssuerHttpsVerified",
    "twoOwnerRosterVerified",
    "recoveredDataChecked",
    "pairCompared",
)


def owner_roster() -> list[JsonObject]:
    """Build the two-owner approval roster a forged report claims."""
    return [
        {
            "actorId": str(uuid.uuid4()),
            "approvalEventId": str(uuid.uuid4()),
            "active": True,
            "role": "owner",
            "grantRevision": revision,
        }
        for revision in (1, 2)
    ]


def forged_full_report(qualified: JsonObject, now: datetime) -> JsonObject:
    """Build a structurally complete restore report that only a forger would sign."""
    report: JsonObject = {
        "format": "claimcore-restore-qualification-1",
        "source": "ClaimCore.Database",
        "scope": "full",
        "realDataReady": False,
        **{
            field: qualified[field]
            for field in (
                "installationId",
                "lineageId",
                "epoch",
                "primarySystemId",
                "primaryTimeline",
                "witnessSystemId",
                "witnessTimeline",
                "custodyObjects",
                "custodyKeyId",
                "custodyPublicKeySha256",
            )
        },
        "cycleId": str(uuid.uuid4()),
        "backupCaptureSequence": 0,
        "backupCaptureHash": "f" * 64,
        "witnessCutoff": 1,
        "witnessCutoffHash": "a" * 64,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "reportSignerKeyId": str(uuid.uuid4()),
        "verifierBinarySha256": "0" * 64,
        "evidenceIndexSha256": "9" * 64,
        "checkpointSha256": "b" * 64,
        "signedInventoryFileSha256": "c" * 64,
        "quiescentBarrierSha256": "d" * 64,
        "catalogManifestSha256": "e" * 64,
        "authorityRevision": 2,
        "authorizedApprovers": owner_roster(),
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=10)),
        "pendingIntents": 0,
    }
    report.update(dict.fromkeys(VERIFIED_FLAGS, True))
    return report


@dataclass
class ForgedReport:
    """A signed report on disk plus the key that signed it."""

    report: JsonObject
    private: Path
    statement: Path
    detached: Path

    def write(self, report: JsonObject | None = None) -> None:
        """Replace the on-disk statement and signature with a signed `report`."""
        body = report or self.report
        envelope = sign(body, self.private)
        self.statement.write_bytes(canonical(body))
        self.detached.write_bytes(base64.b64decode(envelope["signatureBase64"]))
        self.statement.chmod(0o600)
        self.detached.chmod(0o600)


def _install_forged_report(root: Path, deployment: Deployment) -> ForgedReport:
    private, public = keypair(root, "product-verifier")
    report = forged_full_report(deployment.qualified, datetime.now(UTC))
    forged = ForgedReport(
        report, private, root / "forged-full-report.json", root / "forged-full-report.sig"
    )
    forged.write()
    deployment.config.update(
        {
            "installationId": report["installationId"],
            "fullReportPath": str(forged.statement),
            "fullReportSignaturePath": str(forged.detached),
            "fullReportPublicKey": str(public),
            "productVerifierPath": str(root / "missing-product-verifier"),
            "productVerifierSha256": "0" * 64,
        }
    )
    return forged


def _report_refusals(deployment: Deployment, forged: ForgedReport) -> None:
    config = deployment.config
    refuses(lambda: verify_deployment(config), "config-selected-product-verifier")
    config.pop("productVerifierPath")
    original = forged.detached.read_bytes()
    altered = bytearray(original)
    altered[0] ^= 1
    forged.detached.write_bytes(altered)
    refuses(lambda: verify_deployment(config), "deployment-signature-invalid")
    for field, value, category in (
        ("scope", "synthetic-only", "qualification-not-full"),
        ("validUntil", EXPIRED, "qualification-expired"),
    ):
        forged.write({**forged.report, field: value})
        refuses(lambda: verify_deployment(config), category)
    forged.write()


def _hash_pinned_stub_is_not_executed(
    root: Path, deployment: Deployment, forged: ForgedReport
) -> None:
    config = deployment.config
    marker, stub = root / "stub-executed", root / "claimcore-stub"
    stub.write_text(
        "#!/usr/bin/env python3\n"
        "from pathlib import Path\n"
        f"Path({str(marker)!r}).write_text('executed')\n",
        encoding="ascii",
    )
    stub.chmod(0o700)
    stub_hash = hashlib.sha256(stub.read_bytes()).hexdigest()
    forged.report["verifierBinarySha256"] = stub_hash
    config["productVerifierPath"] = str(stub)
    config["productVerifierSha256"] = stub_hash
    forged.write()
    refuses(lambda: verify_deployment(config), "config-selected-product-verifier")
    ensure(not marker.exists(), "A hash-pinned stub was executed as a product verifier.")


def product_report_negative(root: Path, deployment: Deployment) -> None:
    """Forged, altered, stale and config-selected verifier reports must all be refused."""
    forged = _install_forged_report(root, deployment)
    _report_refusals(deployment, forged)
    _hash_pinned_stub_is_not_executed(root, deployment, forged)


NONCE = "e" * 64
REPORT_SHA = "f" * 64


@dataclass
class PublicationFixture:
    """A synthetic published verifier, its signed manifest and the recheck inputs."""

    root: Path
    qualified: JsonObject
    config: JsonObject
    binary: Path
    overrides: ProductOverrides

    def response(self, command: Sequence[str], received: str = NONCE) -> bytes:
        """Answer the fixed verifier command with a synthetic recheck result."""
        ensure(
            command[1] == str(self.binary) and command[2] == "verify-restore-report",
            "product-command-shape",
        )
        return canonical(
            {
                "format": "claimcore-restore-recheck-1",
                "nonce": received,
                "status": "EVIDENCE_RECHECKED",
                "reportSha256": REPORT_SHA,
                "evidenceIndexSha256": self.qualified["evidenceIndexSha256"],
                "witnessCutoff": self.qualified["witnessCutoff"],
                "witnessCutoffHash": self.qualified["witnessCutoffHash"],
                "realDataReady": False,
            }
        )

    def recheck(
        self, *, config: JsonObject | None = None, overrides: ProductOverrides | None = None
    ) -> JsonObject:
        """Recheck the synthetic report with optional config or override changes."""
        return product_recheck(
            config or self.config,
            NONCE,
            REPORT_SHA,
            self.qualified,
            overrides=overrides or self.overrides,
        )


def _publication_manifest(qualified: JsonObject, binary_hash: str, now: datetime) -> JsonObject:
    return {
        "format": "claimcore-publication-manifest-1",
        "publicationId": str(uuid.uuid4()),
        "verifierBinarySha256": binary_hash,
        "reportSignerKeyId": str(uuid.uuid4()),
        "checkpointSignerKeyId": str(uuid.uuid4()),
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
        "writerGeneration": 1,
        "witnessCutoff": qualified["witnessCutoff"],
        "witnessCutoffHash": qualified["witnessCutoffHash"],
        "issuedAt": utc(now - timedelta(minutes=1)),
        "validUntil": utc(now + timedelta(minutes=10)),
    }


def _publication_fixture(root: Path) -> PublicationFixture:
    private, public = keypair(root, "independent-publication")
    binary = private_bytes(root, "synthetic-database.dll", b"synthetic product verifier bytes")
    binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
    qualified = qualification()
    qualified["evidenceIndexSha256"] = "d" * 64
    manifest = _publication_manifest(qualified, binary_hash, datetime.now(UTC))
    files = signed_files(root, "claimcore-publication", manifest, private)
    config: JsonObject = {
        "installationId": qualified["installationId"],
        "productVerifierSha256": binary_hash,
        "fullReportPath": str(
            private_bytes(root, "synthetic-report.json", b"synthetic private evidence")
        ),
        "fullReportSignaturePath": str(
            private_bytes(root, "synthetic-report.sig", b"synthetic private evidence")
        ),
        "restoreEvidenceIndexPath": str(
            private_bytes(root, "synthetic-index.json", b"synthetic private evidence")
        ),
    }
    fixture = PublicationFixture(
        root,
        qualified,
        config,
        binary,
        ProductOverrides(root_key=public, publication_files=files, binary_path=binary),
    )
    fixture.overrides = replace(fixture.overrides, product_runner=fixture.response)
    return fixture


def publication_recheck_synthetic(root: Path) -> None:
    """Recheck evidence under a synthetic root, which must never assert real-data readiness."""
    fixture = _publication_fixture(root)
    ensure(
        fixture.recheck()["syntheticOnly"] is True,
        "Injected root cannot assert real-data readiness.",
    )
    refuses(
        lambda: fixture.recheck(
            config={**fixture.config, "productVerifierPath": str(fixture.binary)}
        ),
        "config-selected-product-verifier",
    )
    stale = replace(
        fixture.overrides, product_runner=lambda command: fixture.response(command, "0" * 64)
    )
    refuses(lambda: fixture.recheck(overrides=stale), "product-recheck-mismatch")
    wrong_root = replace(fixture.overrides, root_key=keypair(root, "wrong-publication")[1])
    refuses(lambda: fixture.recheck(overrides=wrong_root), "deployment-signature-invalid")
    fixture.binary.write_bytes(b"changed synthetic product verifier bytes")
    refuses(fixture.recheck, "published-verifier-digest")
