"""Non-mutating promotion-evidence review; actual cutover remains product-owned."""

import hashlib
import json
import subprocess
import uuid
from datetime import datetime, timezone
from pathlib import Path

from inventory import canonical


class ReviewFailure(Exception):
    pass


def need(value, category):
    if not value:
        raise ReviewFailure(category)


def signed_document(path, public_key, private_path, openssl):
    source = private_path(path)
    signature = private_path(Path(path).with_suffix(".sig"))
    key = private_path(public_key)
    need(0 < source.stat().st_size <= 128 * 1024, "promotion-document-size")
    need(signature.stat().st_size == 64, "promotion-signature-length")
    result = subprocess.run(
        [
            openssl,
            "pkeyutl",
            "-verify",
            "-rawin",
            "-pubin",
            "-inkey",
            str(key),
            "-in",
            str(source),
            "-sigfile",
            str(signature),
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    need(result.returncode == 0, "promotion-signature-invalid")
    body = source.read_bytes()
    document = json.loads(body)
    need(body == canonical(document), "promotion-document-not-canonical")
    return document, hashlib.sha256(body).hexdigest()


def future(value, maximum_seconds):
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (AttributeError, ValueError):
        return False
    if parsed.tzinfo is None:
        return False
    remaining = (parsed - datetime.now(timezone.utc)).total_seconds()
    return 0 < remaining <= maximum_seconds


def review(config, report_path, fence_path, approval_paths, private_path, openssl):
    need(len(approval_paths) == 2, "two-approvals-required")
    report, report_hash = signed_document(
        report_path, config["verificationKey"], private_path, openssl
    )
    need(
        report.get("format") == "claimcore-restore-qualification-1",
        "promotion-report-format",
    )
    need(
        report.get("scope") == "full" and report.get("source") == "ClaimCore.Database",
        "synthetic-report-not-promotable",
    )
    need(
        report.get("catalogVerified") is True
        and report.get("dataAuditVerified") is True
        and report.get("walTimelineVerified") is True,
        "promotion-audit-incomplete",
    )
    need(
        report.get("authorityReconciled") is True
        and report.get("managedCopiesRegistered") is True
        and report.get("pendingIntents") == 0,
        "promotion-authority-incomplete",
    )
    need(
        type(report.get("authorityRevision")) is int
        and report["authorityRevision"] > 0,
        "approver-registry-unverified",
    )
    need(
        type(report.get("witnessCutoff")) is int and report["witnessCutoff"] >= 0,
        "approver-cutoff-mismatch",
    )
    need(future(report.get("validUntil"), 3600), "promotion-report-expired")
    uuid.UUID(report["installationId"])
    uuid.UUID(report["lineageId"])
    fence, fence_hash = signed_document(
        fence_path, config["fenceVerificationKey"], private_path, openssl
    )
    need(
        fence.get("format") == "claimcore-old-writer-fence-1"
        and fence.get("reportSha256") == report_hash,
        "fence-report-mismatch",
    )
    need(
        fence.get("installationId") == report["installationId"]
        and fence.get("lineageId") == report["lineageId"],
        "fence-installation-mismatch",
    )
    need(
        fence.get("oldEpoch") == report["epoch"]
        and type(fence.get("newEpoch")) is int
        and fence["newEpoch"] > report["epoch"],
        "fence-epoch",
    )
    required = (
        "oldWriterStopped",
        "primarySessionsTerminated",
        "witnessSessionsTerminated",
        "oldEndpointIsolated",
        "primaryCredentialRevoked",
        "witnessCredentialRevoked",
        "newEpochWitnessed",
        "preIsolationCommitReconciled",
    )
    need(all(fence.get(field) is True for field in required), "old-writer-not-fenced")
    need(
        fence.get("authorityRevision") == report["authorityRevision"],
        "fence-authority-revision",
    )
    need(future(fence.get("validUntil"), 900), "fence-report-expired")
    approved = set()
    key_ids = set()
    public_keys = set()
    authority_items = report.get("authorizedApprovers", [])
    need(
        isinstance(authority_items, list)
        and 2 <= len(authority_items) <= 1000
        and all(
            isinstance(item, dict)
            and set(item)
            == {"actorId", "approvalEventId", "active", "role", "grantRevision"}
            for item in authority_items
        ),
        "approver-registry-shape",
    )
    authorized = {item.get("actorId"): item for item in authority_items}
    need(len(authorized) == len(authority_items), "approver-registry-duplicate")
    for approval_path in approval_paths:
        raw = json.loads(private_path(approval_path).read_bytes())
        key_id = raw.get("signingKeyId")
        key_entry = config["approverKeys"].get(key_id)
        need(key_entry is not None, "unknown-approver-key")
        approval, _ = signed_document(
            approval_path, key_entry["publicKey"], private_path, openssl
        )
        actor = approval.get("actorId")
        authority = authorized.get(actor)
        need(
            actor == key_entry["actorId"] and authority is not None, "approver-identity"
        )
        need(
            authority.get("active") is True
            and authority.get("role") == "owner"
            and approval.get("approvalEventId") == authority.get("approvalEventId"),
            "approver-not-authorized",
        )
        need(
            type(authority.get("grantRevision")) is int
            and 0 < authority["grantRevision"] <= report["authorityRevision"],
            "approver-grant-revision",
        )
        need(
            approval.get("format") == "claimcore-restore-approval-1"
            and approval.get("action") == "PROMOTE_RESTORED_INSTALLATION",
            "approval-action",
        )
        need(
            approval.get("reportSha256") == report_hash
            and approval.get("fenceSha256") == fence_hash,
            "approval-evidence-mismatch",
        )
        need(
            approval.get("installationId") == report["installationId"]
            and approval.get("newEpoch") == fence["newEpoch"],
            "approval-target-mismatch",
        )
        need(
            approval.get("grantRevision") == authority.get("grantRevision")
            and approval.get("authorityRevision") == report["authorityRevision"]
            and future(approval.get("validUntil"), 3600),
            "approval-stale",
        )
        need(actor not in approved and key_id not in key_ids, "approvers-not-distinct")
        key_fingerprint = hashlib.sha256(
            private_path(key_entry["publicKey"]).read_bytes()
        ).hexdigest()
        need(key_fingerprint not in public_keys, "approver-key-reused")
        approved.add(actor)
        key_ids.add(key_id)
        public_keys.add(key_fingerprint)
    return {
        "status": "synthetic-promotion-evidence-reviewed",
        "installationId": report["installationId"],
        "newEpoch": fence["newEpoch"],
        "approvals": 2,
        "promotionAuthorized": False,
        "productCutoverRequired": True,
    }
