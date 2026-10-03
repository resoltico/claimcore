"""Non-mutating promotion-evidence review; actual cutover remains product-owned."""

import hashlib
import json
import subprocess
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from backup_types import Json, JsonObject
from deployment_common import public_key_identity
from inventory import canonical

DOCUMENT_LIMIT = 128 * 1024
SIGNATURE_BYTES = 64
REPORT_VALIDITY_SECONDS = 3600
FENCE_VALIDITY_SECONDS = 900
APPROVAL_VALIDITY_SECONDS = 3600
REQUIRED_APPROVALS = 2
MAX_APPROVERS = 1000
FENCE_CLAIMS = (
    "oldWriterStopped",
    "primarySessionsTerminated",
    "witnessSessionsTerminated",
    "oldEndpointIsolated",
    "primaryCredentialRevoked",
    "witnessCredentialRevoked",
    "newEpochWitnessed",
    "preIsolationCommitReconciled",
)
APPROVER_FIELDS = {"actorId", "approvalEventId", "active", "role", "grantRevision"}


class ReviewFailureError(Exception):
    """A promotion review refused with a safe category."""


@dataclass(frozen=True)
class ReviewTools:
    """The owner's private-file reader and the openssl executable the review uses."""

    private_path: Callable[[str | Path], Path]
    openssl: str


def need(value: object, category: str) -> None:
    """Refuse with `category` unless `value` is truthy."""
    if not value:
        raise ReviewFailureError(category)


def signed_document(
    path: str | Path, public_key: str | Path, tools: ReviewTools
) -> tuple[JsonObject, str]:
    """Verify the detached signature of a canonical document; return it and its digest."""
    source = tools.private_path(path)
    signature = tools.private_path(Path(path).with_suffix(".sig"))
    key = tools.private_path(public_key)
    need(0 < source.stat().st_size <= DOCUMENT_LIMIT, "promotion-document-size")
    need(signature.stat().st_size == SIGNATURE_BYTES, "promotion-signature-length")
    result = subprocess.run(
        [
            tools.openssl,
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


def future(value: Json, maximum_seconds: int) -> bool:
    """Whether `value` is a timezone-aware time within `maximum_seconds` from now."""
    try:
        parsed = datetime.fromisoformat(value)
    except (TypeError, ValueError):
        return False
    if parsed.tzinfo is None:
        return False
    remaining = (parsed - datetime.now(UTC)).total_seconds()
    return 0 < remaining <= maximum_seconds


def _check_report(report: JsonObject) -> None:
    need(report.get("format") == "claimcore-restore-qualification-1", "promotion-report-format")
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
        type(report.get("authorityRevision")) is int and report["authorityRevision"] > 0,
        "approver-registry-unverified",
    )
    need(
        type(report.get("witnessCutoff")) is int and report["witnessCutoff"] >= 0,
        "approver-cutoff-mismatch",
    )
    need(future(report.get("validUntil"), REPORT_VALIDITY_SECONDS), "promotion-report-expired")
    uuid.UUID(report["installationId"])
    uuid.UUID(report["lineageId"])


def _check_fence(fence: JsonObject, report: JsonObject, report_hash: str) -> None:
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
    need(all(fence.get(field) is True for field in FENCE_CLAIMS), "old-writer-not-fenced")
    need(fence.get("authorityRevision") == report["authorityRevision"], "fence-authority-revision")
    need(future(fence.get("validUntil"), FENCE_VALIDITY_SECONDS), "fence-report-expired")


def _authorized_owners(report: JsonObject) -> dict[str, JsonObject]:
    items = report.get("authorizedApprovers", [])
    need(
        isinstance(items, list)
        and REQUIRED_APPROVALS <= len(items) <= MAX_APPROVERS
        and all(isinstance(item, dict) and set(item) == APPROVER_FIELDS for item in items),
        "approver-registry-shape",
    )
    authorized = {item.get("actorId"): item for item in items}
    need(len(authorized) == len(items), "approver-registry-duplicate")
    return authorized


@dataclass
class _Approvers:
    """The approvers, keys and key fingerprints already accepted."""

    actors: set[str]
    key_ids: set[str]
    fingerprints: set[str]


def _present[T](value: T | None, category: str) -> T:
    """Return `value`, refusing with `category` when it is absent."""
    if value is None:
        raise ReviewFailureError(category)
    return value


def _check_authority(approval: JsonObject, report: JsonObject, key_entry: JsonObject) -> JsonObject:
    actor: Json = approval.get("actorId")
    authority = _present(_authorized_owners(report).get(actor), "approver-identity")
    need(actor == key_entry["actorId"], "approver-identity")
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
    return authority


def _check_approval_content(
    approval: JsonObject,
    authority: JsonObject,
    evidence: tuple[JsonObject, JsonObject, str, str],
) -> None:
    report, fence, report_hash, fence_hash = evidence
    need(
        approval.get("format") == "claimcore-restore-approval-1"
        and approval.get("action") == "PROMOTE_RESTORED_INSTALLATION",
        "approval-action",
    )
    need(
        approval.get("reportSha256") == report_hash and approval.get("fenceSha256") == fence_hash,
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
        and future(approval.get("validUntil"), APPROVAL_VALIDITY_SECONDS),
        "approval-stale",
    )


def _check_approval(
    approval_path: str | Path,
    evidence: tuple[JsonObject, JsonObject, str, str],
    config: JsonObject,
    tools: ReviewTools,
    seen: _Approvers,
) -> None:
    raw = json.loads(tools.private_path(approval_path).read_bytes())
    key_id: Json = raw.get("signingKeyId")
    key_entry = _present(config["approverKeys"].get(key_id), "unknown-approver-key")
    approval, _ = signed_document(approval_path, key_entry["publicKey"], tools)
    authority = _check_authority(approval, evidence[0], key_entry)
    _check_approval_content(approval, authority, evidence)
    actor: Json = approval.get("actorId")
    need(actor not in seen.actors and key_id not in seen.key_ids, "approvers-not-distinct")
    fingerprint = public_key_identity(tools.private_path(key_entry["publicKey"])).hex()
    need(fingerprint not in seen.fingerprints, "approver-key-reused")
    seen.actors.add(actor)
    seen.key_ids.add(key_id)
    seen.fingerprints.add(fingerprint)


def review(
    config: JsonObject,
    report_path: str | Path,
    fence_path: str | Path,
    approval_paths: Sequence[str | Path],
    tools: ReviewTools,
) -> JsonObject:
    """Review promotion evidence without mutating anything."""
    need(len(approval_paths) == REQUIRED_APPROVALS, "two-approvals-required")
    report, report_hash = signed_document(report_path, config["verificationKey"], tools)
    _check_report(report)
    fence, fence_hash = signed_document(fence_path, config["fenceVerificationKey"], tools)
    _check_fence(fence, report, report_hash)
    seen = _Approvers(set(), set(), set())
    for approval_path in approval_paths:
        _check_approval(
            approval_path, (report, fence, report_hash, fence_hash), config, tools, seen
        )
    return {
        "status": "synthetic-promotion-evidence-reviewed",
        "installationId": report["installationId"],
        "newEpoch": fence["newEpoch"],
        "approvals": REQUIRED_APPROVALS,
        "promotionAuthorized": False,
        "productCutoverRequired": True,
    }
