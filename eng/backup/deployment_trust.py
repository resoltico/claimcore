"""Read-only deploy-time custody and restored-pair evidence checks."""

from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from backup_types import JsonObject
from deployment_aggregate_model import AVAILABILITY
from deployment_common import is_sha256, is_uuid, require, timestamp, verify

ROLES = ("primary", "witness", "archive", "checkpoint", "key")
MAX_SKEW_SECONDS = 30
MAX_VALIDITY_SECONDS = 90
MIN_FINAL_OBJECTS = 2
MAX_FINAL_OBJECTS = 2000
PINNED_FIELDS = ("machineHash", "storageHash", "adminActorId", "hostKeyId")


@dataclass(frozen=True)
class ProbeExpectation:
    """The challenge and qualification every role probe must answer."""

    nonce: str
    qualification_sha: str
    key_proof: str
    qualification: JsonObject


def _check_freshness(report: JsonObject) -> None:
    issued, expires = timestamp(report.get("issuedAt")), timestamp(report.get("expiresAt"))
    now = datetime.now(UTC)
    require(abs((now - issued).total_seconds()) <= MAX_SKEW_SECONDS, "probe-not-fresh")
    require(issued < expires <= issued + timedelta(seconds=MAX_VALIDITY_SECONDS), "probe-expiry")
    require(now < expires, "probe-expired")


def _check_pins(
    report: JsonObject, role: str, pinned: JsonObject, expect: ProbeExpectation
) -> None:
    qualification = expect.qualification
    require(report.get("containerized") is False, "container-not-independent-host")
    require(
        report.get("installationId") == qualification["installationId"]
        and report.get("lineageId") == qualification["lineageId"]
        and report.get("epoch") == qualification["epoch"],
        "probe-installation-mismatch",
    )
    require(
        report.get("availabilityKind") == AVAILABILITY[role] and report.get("available") is True,
        "probe-resource-unavailable",
    )
    for field in PINNED_FIELDS:
        require(report.get(field) == pinned[field], "probe-not-owner-pinned")
    require(
        is_sha256(report["machineHash"]) and is_sha256(report["storageHash"]), "probe-fingerprint"
    )
    require(
        is_uuid(report["adminActorId"]) and is_uuid(report["hostKeyId"]),
        "probe-custodian-identity",
    )


def _check_key(report: JsonObject, pinned: JsonObject, expect: ProbeExpectation) -> None:
    qualification = expect.qualification
    require(report.get("keyChallengeProofSha256") == expect.key_proof, "key-custody-proof")
    require(
        report.get("custodyKeyId") == pinned["custodyKeyId"] == qualification["custodyKeyId"]
        and report.get("custodyPublicKeySha256")
        == pinned["custodyPublicKeySha256"]
        == qualification["custodyPublicKeySha256"],
        "probe-key-identity",
    )
    require(report.get("softwareKeyExportable") is True, "probe-key-kind")


def _check_cluster(
    report: JsonObject, role: str, pinned: JsonObject, qualification: JsonObject
) -> None:
    require(
        report.get("postgresSystemId")
        == pinned["postgresSystemId"]
        == qualification[role + "SystemId"]
        and report.get("timeline") == pinned["timeline"] == qualification[role + "Timeline"],
        "probe-cluster-identity",
    )
    if role == "witness":
        require(
            report.get("witnessTipSequence") == qualification["witnessCutoff"]
            and report.get("witnessTipHash") == qualification["witnessCutoffHash"],
            "probe-witness-tip",
        )


def _check_retained_copy(
    report: JsonObject, role: str, pinned: JsonObject, qualification: JsonObject
) -> None:
    expected = qualification["custodyObjects"][role]
    require(
        report.get("objectId") == pinned["objectId"] == expected["objectId"]
        and report.get("objectSha256") == pinned["objectSha256"] == expected["sha256"]
        and report.get("objectBytes") == pinned["objectBytes"] == expected["bytes"],
        "probe-object-unrelated",
    )
    if role == "archive":
        final_objects = qualification.get("finalWalObjects")
        require(
            isinstance(final_objects, list)
            and MIN_FINAL_OBJECTS <= len(final_objects) <= MAX_FINAL_OBJECTS
            and report.get("finalWalObjects") == final_objects
            and report.get("finalWalObjectCount") == len(final_objects)
            and report.get("finalWalObjectSha256") == qualification.get("finalWalObjectSha256"),
            "probe-final-wal-incomplete",
        )


def _probe(
    config: JsonObject, role: str, envelope: JsonObject, expect: ProbeExpectation
) -> JsonObject:
    pinned = config["roles"][role]
    report, _ = verify(envelope, pinned["probePublicKey"])
    require(report.get("format") == "claimcore-deployment-probe-1", "probe-format")
    require(report.get("role") == role and report.get("nonce") == expect.nonce, "probe-challenge")
    require(report.get("qualificationSha256") == expect.qualification_sha, "probe-qualification")
    _check_freshness(report)
    _check_pins(report, role, pinned, expect)
    if role == "key":
        _check_key(report, pinned, expect)
    else:
        require(report.get("keyChallengeProofSha256") is None, "probe-excess-key-proof")
    if role in ("primary", "witness"):
        _check_cluster(report, role, pinned, expect.qualification)
    if role in ("archive", "checkpoint"):
        _check_retained_copy(report, role, pinned, expect.qualification)
    return report


def evaluate(
    config: JsonObject,
    envelopes: dict[str, JsonObject],
    expect: ProbeExpectation,
    *,
    live_transport: bool,
) -> dict[str, bool | str]:
    """Corroborate every role's owner-pinned probe against the qualification report."""
    require(config.get("format") == "claimcore-deployment-1", "deployment-config-format")
    require(
        set(config.get("roles", {})) == set(ROLES) and set(envelopes) == set(ROLES),
        "deployment-roles",
    )
    require(
        is_sha256(expect.nonce)
        and is_sha256(expect.qualification_sha)
        and is_sha256(expect.key_proof),
        "deployment-challenge",
    )
    reports = {role: _probe(config, role, envelopes[role], expect) for role in ROLES}
    require(
        reports["primary"]["postgresSystemId"] != reports["witness"]["postgresSystemId"],
        "clusters-not-independent",
    )
    for name in PINNED_FIELDS:
        require(
            len({report[name] for report in reports.values()}) == len(ROLES),
            "custody-not-independent",
        )
    require(config.get("mode") == "production" and live_transport, "local-only-qualification")
    return {
        "status": "topology-evidence-corroborated",
        "realDataReady": False,
        "scope": "owner-pinned-probe-only",
    }
