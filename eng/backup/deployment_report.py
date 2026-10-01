"""Signed pre-handoff restored-pair report, never a cutover certificate alone."""

import base64
import json
import re
from datetime import UTC, datetime, timedelta

from backup_types import Json, JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    canonical,
    is_sha256,
    is_uuid,
    private_path,
    require,
    timestamp,
    verify,
)

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


REPORT_LIMIT = 128 * 1024
MIN_OWNERS = 2
MAX_OWNERS = 1000
VALIDITY_HOURS = 1
DIGEST_FIELDS = (
    "backupCaptureHash",
    "witnessCutoffHash",
    "verifierBinarySha256",
    "evidenceIndexSha256",
    "checkpointSha256",
    "signedInventoryFileSha256",
    "quiescentBarrierSha256",
    "catalogManifestSha256",
    "custodyPublicKeySha256",
)
IDENTITY_FIELDS = ("installationId", "lineageId", "cycleId", "reportSignerKeyId", "custodyKeyId")


def _lsn(value: Json) -> bool:
    return (
        isinstance(value, str) and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def _custody_objects(value: Json) -> None:
    require(
        isinstance(value, dict) and set(value) == {"archive", "checkpoint"},
        "qualification-copy-inventory",
    )
    for item in value.values():
        require(
            isinstance(item, dict)
            and set(item) == {"objectId", "sha256", "bytes"}
            and is_uuid(item["objectId"])
            and is_sha256(item["sha256"])
            and type(item["bytes"]) is int
            and item["bytes"] > 0,
            "qualification-copy-inventory",
        )


def _owners(value: Json, revision: int) -> None:
    require(
        isinstance(value, list) and MIN_OWNERS <= len(value) <= MAX_OWNERS,
        "qualification-owner-roster",
    )
    actors, approvals = set(), set()
    for item in value:
        require(
            isinstance(item, dict)
            and set(item) == {"actorId", "approvalEventId", "active", "role", "grantRevision"}
            and is_uuid(item["actorId"])
            and is_uuid(item["approvalEventId"])
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


def _check_claims(verified: JsonObject, config: JsonObject) -> None:
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
    for name in IDENTITY_FIELDS:
        require(is_uuid(verified[name]), "qualification-identity")
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
    for name in DIGEST_FIELDS:
        require(is_sha256(verified[name]), "qualification-evidence-digest")


def _check_clusters(verified: JsonObject) -> None:
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


def _check_freshness(verified: JsonObject, config: JsonObject) -> None:
    checked, expires = timestamp(verified["checkedAt"]), timestamp(verified["validUntil"])
    now = datetime.now(UTC)
    window = timedelta(hours=VALIDITY_HOURS)
    require(checked <= now < expires <= checked + window, "qualification-expired")
    require(now - checked <= window, "qualification-expired")
    require(
        verified["verifierBinarySha256"] == config["productVerifierSha256"],
        "qualification-verifier-identity",
    )


def pre_handoff_report(config: JsonObject) -> tuple[JsonObject, str]:
    """Verify the signed restore qualification report and return it with its digest."""
    source = private_path(config["fullReportPath"])
    signature = private_path(config["fullReportSignaturePath"])
    raw = source.read_bytes()
    signed = signature.read_bytes()
    require(0 < len(raw) <= REPORT_LIMIT and len(signed) == SIGNATURE_BYTES, "qualification-size")
    report = json.loads(raw)
    require(
        isinstance(report, dict) and raw == canonical(report),
        "qualification-not-canonical",
    )
    verified, digest = verify(
        {"report": report, "signatureBase64": base64.b64encode(signed).decode("ascii")},
        config["fullReportPublicKey"],
    )
    _check_claims(verified, config)
    _check_clusters(verified)
    _check_freshness(verified, config)
    return verified, digest
