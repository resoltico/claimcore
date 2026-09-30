"""Pinned independent old-writer fence observation; five new-host probes are insufficient."""

from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_common import require, timestamp, verify

MAX_SKEW_SECONDS = 30
MAX_VALIDITY_SECONDS = 90

FIELDS = {
    "format",
    "role",
    "nonce",
    "installationId",
    "lineageId",
    "epoch",
    "oldGeneration",
    "newGeneration",
    "w1Sequence",
    "w1Hash",
    "reportSha256",
    "fenceReportSha256",
    "oldEndpointId",
    "oldEndpointAddressSha256",
    "oldPrimaryRoleOid",
    "oldWitnessRoleOid",
    "oldPrimaryCredentialSha256",
    "oldWitnessCredentialSha256",
    "primarySessionSetSha256",
    "witnessSessionSetSha256",
    "routeClosed",
    "primarySessionsZero",
    "witnessSessionsZero",
    "primaryCredentialDenied",
    "witnessCredentialDenied",
    "containerized",
    "machineHash",
    "storageHash",
    "adminActorId",
    "hostKeyId",
    "checkedAt",
    "validUntil",
}
PINNED = (
    "machineHash",
    "storageHash",
    "adminActorId",
    "hostKeyId",
    "oldEndpointId",
    "oldEndpointAddressSha256",
    "oldPrimaryRoleOid",
    "oldWitnessRoleOid",
    "oldPrimaryCredentialSha256",
    "oldWitnessCredentialSha256",
    "primarySessionSetSha256",
    "witnessSessionSetSha256",
)
CHECKS = (
    "routeClosed",
    "primarySessionsZero",
    "witnessSessionsZero",
    "primaryCredentialDenied",
    "witnessCredentialDenied",
)


@dataclass(frozen=True)
class ObserverExpectation:
    """What an old-writer fence observation must bind to."""

    pinned: JsonObject
    nonce: str
    report_sha: str
    fence_sha: str
    tail: JsonObject
    fence: JsonObject


def _check_pins(observed: JsonObject, expected: ObserverExpectation) -> None:
    for name in PINNED:
        require(observed[name] == expected.pinned[name], "old-writer-observer-pin")
        if name in expected.fence:
            require(observed[name] == expected.fence[name], "old-writer-fence-identity")


def _check_links(observed: JsonObject, expected: ObserverExpectation) -> None:
    tail = expected.tail
    require(
        observed["installationId"] == tail["installationId"]
        and observed["lineageId"] == tail["lineageId"]
        and observed["epoch"] == tail["epoch"]
        and observed["oldGeneration"] == tail["oldGeneration"]
        and observed["newGeneration"] == tail["newGeneration"]
        and observed["w1Sequence"] == tail["w1Sequence"]
        and observed["w1Hash"] == tail["w1Hash"]
        and observed["reportSha256"] == expected.report_sha
        and observed["fenceReportSha256"] == expected.fence_sha,
        "old-writer-observation-link",
    )


def _check_freshness(observed: JsonObject, now: datetime | None) -> None:
    checked, until = timestamp(observed["checkedAt"]), timestamp(observed["validUntil"])
    current = now or datetime.now(UTC)
    require(
        abs((current - checked).total_seconds()) <= MAX_SKEW_SECONDS, "old-writer-observation-stale"
    )
    require(
        checked < until <= checked + timedelta(seconds=MAX_VALIDITY_SECONDS) and current < until,
        "old-writer-observation-expired",
    )


def verify_observer(
    envelope: JsonObject,
    public_key: str | Path,
    expected: ObserverExpectation,
    *,
    now: datetime | None = None,
) -> tuple[JsonObject, str]:
    """Verify the observer's signed observation against the expected pins and links."""
    observed, digest = verify(envelope, public_key)
    require(set(observed) == FIELDS, "old-writer-observation-shape")
    require(
        observed["format"] == "claimcore-old-writer-fence-observation-1"
        and observed["role"] == "old-writer-fence"
        and observed["nonce"] == expected.nonce
        and observed["containerized"] is False
        and all(observed[name] is True for name in CHECKS),
        "old-writer-not-fenced",
    )
    _check_pins(observed, expected)
    _check_links(observed, expected)
    _check_freshness(observed, now)
    return observed, digest
