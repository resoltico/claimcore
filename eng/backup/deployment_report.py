"""Signed pre-handoff restored-pair report, never a cutover certificate alone."""

import base64
import json
import re
import uuid
from datetime import datetime, timedelta, timezone

from deployment_common import canonical, private_path, require, timestamp, verify

FIELDS = {
    "format",
    "source",
    "scope",
    "realDataReady",
    "cycleId",
    "backupCaptureSequence",
    "backupCaptureHash",
    "installationId",
    "lineageId",
    "epoch",
    "witnessCutoff",
    "witnessCutoffHash",
    "primarySystemId",
    "primaryTimeline",
    "witnessSystemId",
    "witnessTimeline",
    "primaryRegisteredWalHorizon",
    "witnessRegisteredWalHorizon",
    "reportSignerKeyId",
    "verifierBinarySha256",
    "evidenceIndexSha256",
    "checkpointSha256",
    "signedInventoryFileSha256",
    "quiescentBarrierSha256",
    "catalogManifestSha256",
    "authorityRevision",
    "checkedAt",
    "validUntil",
    "recoveredDataChecked",
    "pairCompared",
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
    "pendingIntents",
    "custodyObjects",
    "custodyKeyId",
    "custodyPublicKeySha256",
    "authorizedApprovers",
}

TRUE_CLAIMS = (
    "recoveredDataChecked",
    "pairCompared",
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
)


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _lsn(value):
    return (
        isinstance(value, str)
        and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def _custody_objects(value):
    require(
        isinstance(value, dict) and set(value) == {"archive", "checkpoint"},
        "qualification-copy-inventory",
    )
    for item in value.values():
        require(
            isinstance(item, dict)
            and set(item) == {"objectId", "sha256", "bytes"}
            and _uuid(item["objectId"])
            and _digest(item["sha256"])
            and type(item["bytes"]) is int
            and item["bytes"] > 0,
            "qualification-copy-inventory",
        )


def _owners(value, revision):
    require(
        isinstance(value, list) and 2 <= len(value) <= 1000,
        "qualification-owner-roster",
    )
    actors, approvals = set(), set()
    for item in value:
        require(
            isinstance(item, dict)
            and set(item)
            == {"actorId", "approvalEventId", "active", "role", "grantRevision"}
            and _uuid(item["actorId"])
            and _uuid(item["approvalEventId"])
            and item["active"] is True
            and item["role"] == "owner"
            and type(item["grantRevision"]) is int
            and 1 <= item["grantRevision"] <= revision,
            "qualification-owner-roster",
        )
        actors.add(item["actorId"])
        approvals.add(item["approvalEventId"])
    require(
        len(actors) == len(value) and len(approvals) == len(value),
        "qualification-owner-roster",
    )


def pre_handoff_report(config):
    source = private_path(config["fullReportPath"])
    signature = private_path(config["fullReportSignaturePath"])
    raw = source.read_bytes()
    signed = signature.read_bytes()
    require(0 < len(raw) <= 128 * 1024 and len(signed) == 64, "qualification-size")
    report = json.loads(raw)
    require(
        isinstance(report, dict) and raw == canonical(report),
        "qualification-not-canonical",
    )
    verified, digest = verify(
        {"report": report, "signatureBase64": base64.b64encode(signed).decode("ascii")},
        config["fullReportPublicKey"],
    )
    require(set(verified) == FIELDS, "qualification-shape")
    require(
        verified["format"] == "claimcore-restore-qualification-1"
        and verified["source"] == "ClaimCore.Database"
        and verified["scope"] == "full"
        and verified["realDataReady"] is False,
        "qualification-not-full",
    )
    for name in TRUE_CLAIMS:
        require(verified[name] is True, "qualification-check-missing")
    require(
        type(verified["pendingIntents"]) is int and verified["pendingIntents"] == 0,
        "qualification-pending-intent",
    )
    for name in (
        "installationId",
        "lineageId",
        "cycleId",
        "reportSignerKeyId",
        "custodyKeyId",
    ):
        require(_uuid(verified[name]), "qualification-identity")
    require(
        verified["installationId"] == config["installationId"]
        and verified["lineageId"] == config["lineageId"]
        and verified["epoch"] == config["epoch"]
        and type(verified["epoch"]) is int
        and verified["epoch"] > 0,
        "qualification-installation",
    )
    require(
        type(verified["backupCaptureSequence"]) is int
        and type(verified["witnessCutoff"]) is int
        and 0 <= verified["backupCaptureSequence"] <= verified["witnessCutoff"],
        "qualification-cutoff",
    )
    for name in (
        "backupCaptureHash",
        "witnessCutoffHash",
        "verifierBinarySha256",
        "evidenceIndexSha256",
        "checkpointSha256",
        "signedInventoryFileSha256",
        "quiescentBarrierSha256",
        "catalogManifestSha256",
        "custodyPublicKeySha256",
    ):
        require(_digest(verified[name]), "qualification-evidence-digest")
    for cluster in ("primary", "witness"):
        require(
            isinstance(verified[cluster + "SystemId"], str)
            and re.fullmatch(r"[0-9]{1,20}", verified[cluster + "SystemId"])
            and type(verified[cluster + "Timeline"]) is int
            and verified[cluster + "Timeline"] > 0
            and _lsn(verified[cluster + "RegisteredWalHorizon"]),
            "qualification-cluster-identity",
        )
    require(
        verified["primarySystemId"] != verified["witnessSystemId"],
        "qualification-shared-cluster",
    )
    _custody_objects(verified["custodyObjects"])
    revision = verified["authorityRevision"]
    require(type(revision) is int and revision > 0, "qualification-authority")
    _owners(verified["authorizedApprovers"], revision)
    checked, expires = (
        timestamp(verified["checkedAt"]),
        timestamp(verified["validUntil"]),
    )
    now = datetime.now(timezone.utc)
    require(
        checked <= now < expires <= checked + timedelta(hours=1),
        "qualification-expired",
    )
    require(now - checked <= timedelta(hours=1), "qualification-expired")
    require(
        verified["verifierBinarySha256"] == config["productVerifierSha256"],
        "qualification-verifier-identity",
    )
    return verified, digest
