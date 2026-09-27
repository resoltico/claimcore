"""Pinned independent old-writer fence observation; five new-host probes are insufficient."""

from datetime import datetime, timedelta, timezone

from deployment_common import require, timestamp, verify

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
CHECKS = (
    "routeClosed",
    "primarySessionsZero",
    "witnessSessionsZero",
    "primaryCredentialDenied",
    "witnessCredentialDenied",
)


def verify_observer(
    envelope, public_key, pinned, nonce, report_sha, fence_sha, tail, fence, *, now=None
):
    observed, digest = verify(envelope, public_key)
    require(set(observed) == FIELDS, "old-writer-observation-shape")
    require(
        observed["format"] == "claimcore-old-writer-fence-observation-1"
        and observed["role"] == "old-writer-fence"
        and observed["nonce"] == nonce
        and observed["containerized"] is False
        and all(observed[name] is True for name in CHECKS),
        "old-writer-not-fenced",
    )
    for name in (
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
    ):
        require(observed[name] == pinned[name], "old-writer-observer-pin")
        if name in fence:
            require(observed[name] == fence[name], "old-writer-fence-identity")
    require(
        observed["installationId"] == tail["installationId"]
        and observed["lineageId"] == tail["lineageId"]
        and observed["epoch"] == tail["epoch"]
        and observed["oldGeneration"] == tail["oldGeneration"]
        and observed["newGeneration"] == tail["newGeneration"]
        and observed["w1Sequence"] == tail["w1Sequence"]
        and observed["w1Hash"] == tail["w1Hash"]
        and observed["reportSha256"] == report_sha
        and observed["fenceReportSha256"] == fence_sha,
        "old-writer-observation-link",
    )
    checked, until = timestamp(observed["checkedAt"]), timestamp(observed["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(abs((now - checked).total_seconds()) <= 30, "old-writer-observation-stale")
    require(
        checked < until <= checked + timedelta(seconds=90) and now < until,
        "old-writer-observation-expired",
    )
    return observed, digest
