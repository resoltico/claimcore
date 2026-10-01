"""Signed W1 isolation and physical tail claims for pre-activation review."""

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
from deployment_fenced_objects import check_objects, final_objects_digest

FENCE_FIELDS = {
    "format",
    "installationId",
    "lineageId",
    "epoch",
    "oldGeneration",
    "newGeneration",
    "reportSha256",
    "independentProbeSha256",
    "checkpointSignerKeyId",
    "oldWriterStopped",
    "primarySessionsTerminated",
    "witnessSessionsTerminated",
    "oldEndpointIsolated",
    "primaryCredentialRevoked",
    "witnessCredentialRevoked",
    "preIsolationCommitReconciled",
    "checkedAt",
    "validUntil",
    "oldEndpointId",
    "oldEndpointAddressSha256",
    "oldPrimaryRoleOid",
    "oldWitnessRoleOid",
    "oldPrimaryCredentialSha256",
    "oldWitnessCredentialSha256",
    "primarySessionSetSha256",
    "witnessSessionSetSha256",
}
FENCE_CHECKS = (
    "oldWriterStopped",
    "primarySessionsTerminated",
    "witnessSessionsTerminated",
    "oldEndpointIsolated",
    "primaryCredentialRevoked",
    "witnessCredentialRevoked",
    "preIsolationCommitReconciled",
)
TAIL_FIELDS = {
    "format",
    "scope",
    "realDataReady",
    "recoveryTailSealed",
    "installationId",
    "lineageId",
    "epoch",
    "oldGeneration",
    "newGeneration",
    "handoffId",
    "w1Sequence",
    "w1Hash",
    "reportSha256",
    "fenceReportSha256",
    "checkpointSignerKeyId",
    "primaryRegisteredWalHorizon",
    "witnessRegisteredWalHorizon",
    "primaryFinalWalEndpoint",
    "witnessFinalWalEndpoint",
    "archiveRoot",
    "walObjects",
    "checkedAt",
    "validUntil",
}


EVIDENCE_LIMIT = 1024 * 1024
WINDOW_MINUTES = 15
MAX_ROLE_ID = 2**32 - 1
LSN_HIGH_SHIFT = 32
REQUIRED_CONFIG = (
    "fenceReportPath",
    "fenceReportSignaturePath",
    "fencedTailPath",
    "fencedTailSignaturePath",
    "checkpointPublicKey",
    "restoreArchiveRoot",
)


def _lsn(value: Json) -> bool:
    return (
        isinstance(value, str) and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def _position(value: str) -> int:
    high, low = value.split("/")
    return (int(high, 16) << LSN_HIGH_SHIFT) + int(low, 16)


def _signed(config: JsonObject, source_name: str, signature_name: str) -> tuple[JsonObject, str]:
    source = private_path(config[source_name])
    signature = private_path(config[signature_name])
    raw, signed = source.read_bytes(), signature.read_bytes()
    require(
        0 < len(raw) <= EVIDENCE_LIMIT and len(signed) == SIGNATURE_BYTES, "fenced-evidence-size"
    )
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document),
        "fenced-evidence-canonical",
    )
    return verify(
        {
            "report": document,
            "signatureBase64": base64.b64encode(signed).decode("ascii"),
        },
        config["checkpointPublicKey"],
    )


def _time_window(document: JsonObject) -> datetime:
    checked, until = timestamp(document["checkedAt"]), timestamp(document["validUntil"])
    now = datetime.now(UTC)
    require(
        checked <= now < until <= checked + timedelta(minutes=WINDOW_MINUTES),
        "fenced-evidence-expired",
    )
    return checked


def _check_fence_identity(fence: JsonObject) -> None:
    require(is_uuid(fence["oldEndpointId"]), "writer-fence-old-endpoint")
    for name in (
        "oldEndpointAddressSha256",
        "oldPrimaryCredentialSha256",
        "oldWitnessCredentialSha256",
        "primarySessionSetSha256",
        "witnessSessionSetSha256",
    ):
        require(is_sha256(fence[name]), "writer-fence-old-identity")
    for name in ("oldPrimaryRoleOid", "oldWitnessRoleOid"):
        require(
            type(fence[name]) is int and 0 < fence[name] <= MAX_ROLE_ID,
            "writer-fence-old-role",
        )
    require(
        type(fence["oldGeneration"]) is int
        and fence["oldGeneration"] > 0
        and type(fence["newGeneration"]) is int
        and fence["newGeneration"] == fence["oldGeneration"] + 1,
        "writer-fence-generation",
    )
    require(
        is_uuid(fence["checkpointSignerKeyId"]) and is_sha256(fence["independentProbeSha256"]),
        "writer-fence-identity",
    )


def _fence(config: JsonObject, report: JsonObject, report_sha: str) -> tuple[JsonObject, str]:
    fence, digest = _signed(config, "fenceReportPath", "fenceReportSignaturePath")
    require(set(fence) == FENCE_FIELDS, "writer-fence-shape")
    require(fence["format"] == "claimcore-old-writer-isolation-1", "writer-fence-format")
    require(all(fence[name] is True for name in FENCE_CHECKS), "writer-fence-incomplete")
    require(
        fence["installationId"] == report["installationId"]
        and fence["lineageId"] == report["lineageId"]
        and fence["epoch"] == report["epoch"]
        and fence["reportSha256"] == report_sha,
        "writer-fence-installation",
    )
    _check_fence_identity(fence)
    _time_window(fence)
    return fence, digest


def _check_tail_links(
    tail: JsonObject, report: JsonObject, fence: JsonObject, shas: tuple[str, str]
) -> None:
    report_sha, fence_sha = shas
    require(set(tail) == TAIL_FIELDS, "fenced-tail-shape")
    require(
        tail["format"] == "claimcore-fenced-recovery-tail-1"
        and tail["scope"] == "full"
        and tail["realDataReady"] is False
        and tail["recoveryTailSealed"] is True,
        "fenced-tail-format",
    )
    require(
        tail["installationId"] == report["installationId"]
        and tail["lineageId"] == report["lineageId"]
        and tail["epoch"] == report["epoch"]
        and tail["oldGeneration"] == fence["oldGeneration"]
        and tail["newGeneration"] == fence["newGeneration"]
        and tail["reportSha256"] == report_sha
        and tail["fenceReportSha256"] == fence_sha
        and tail["checkpointSignerKeyId"] == fence["checkpointSignerKeyId"],
        "fenced-tail-link",
    )
    require(
        is_uuid(tail["handoffId"])
        and type(tail["w1Sequence"]) is int
        and tail["w1Sequence"] > report["witnessCutoff"]
        and is_sha256(tail["w1Hash"]),
        "fenced-tail-w1",
    )


def _check_tail_horizons(tail: JsonObject, report: JsonObject) -> None:
    for cluster in ("primary", "witness"):
        horizon = tail[cluster + "RegisteredWalHorizon"]
        final = tail[cluster + "FinalWalEndpoint"]
        require(
            horizon == report[cluster + "RegisteredWalHorizon"]
            and _lsn(final)
            and _position(final) >= _position(horizon),
            "fenced-tail-horizon",
        )


def fenced_documents(config: JsonObject, report: JsonObject, report_sha: str) -> JsonObject:
    """Verify the signed old-writer fence and recovery tail against the qualification report."""
    require(all(name in config for name in REQUIRED_CONFIG), "fenced-evidence-config")
    fence, fence_sha = _fence(config, report, report_sha)
    tail, tail_sha = _signed(config, "fencedTailPath", "fencedTailSignaturePath")
    _check_tail_links(tail, report, fence, (report_sha, fence_sha))
    _check_tail_horizons(tail, report)
    require(tail["archiveRoot"] == config["restoreArchiveRoot"], "fenced-tail-archive-root")
    _time_window(tail)
    check_objects(tail, report)
    return {
        "fence": fence,
        "fenceSha256": fence_sha,
        "tail": tail,
        "tailSha256": tail_sha,
        "finalWalObjectSha256": final_objects_digest(tail),
    }
