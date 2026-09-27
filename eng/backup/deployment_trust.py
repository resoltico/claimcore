"""Read-only deploy-time custody and restored-pair evidence checks."""

import re
import uuid
from datetime import datetime, timedelta, timezone

from deployment_common import require, timestamp, verify

ROLES = ("primary", "witness", "archive", "checkpoint", "key")
AVAILABILITY = {
    "primary": "database-read",
    "witness": "database-read",
    "archive": "retained-copy",
    "checkpoint": "retained-copy",
    "key": "key-possession",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _pinned(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _probe(config, role, envelope, nonce, qualification_sha, key_proof, qualification):
    pinned = config["roles"][role]
    report, _ = verify(envelope, pinned["probePublicKey"])
    require(report.get("format") == "claimcore-deployment-probe-1", "probe-format")
    require(
        report.get("role") == role and report.get("nonce") == nonce, "probe-challenge"
    )
    require(
        report.get("qualificationSha256") == qualification_sha, "probe-qualification"
    )
    issued = timestamp(report.get("issuedAt"))
    expires = timestamp(report.get("expiresAt"))
    now = datetime.now(timezone.utc)
    require(abs((now - issued).total_seconds()) <= 30, "probe-not-fresh")
    require(issued < expires <= issued + timedelta(seconds=90), "probe-expiry")
    require(now < expires, "probe-expired")
    require(report.get("containerized") is False, "container-not-independent-host")
    require(
        report.get("installationId") == qualification["installationId"]
        and report.get("lineageId") == qualification["lineageId"]
        and report.get("epoch") == qualification["epoch"],
        "probe-installation-mismatch",
    )
    require(
        report.get("availabilityKind") == AVAILABILITY[role]
        and report.get("available") is True,
        "probe-resource-unavailable",
    )
    for field, expected in (
        ("machineHash", pinned["machineHash"]),
        ("storageHash", pinned["storageHash"]),
        ("adminActorId", pinned["adminActorId"]),
        ("hostKeyId", pinned["hostKeyId"]),
    ):
        require(report.get(field) == expected, "probe-not-owner-pinned")
    require(
        _pinned(report["machineHash"]) and _pinned(report["storageHash"]),
        "probe-fingerprint",
    )
    require(
        _uuid(report["adminActorId"]) and _uuid(report["hostKeyId"]),
        "probe-custodian-identity",
    )
    if role == "key":
        require(report.get("keyChallengeProofSha256") == key_proof, "key-custody-proof")
        require(
            report.get("custodyKeyId")
            == pinned["custodyKeyId"]
            == qualification["custodyKeyId"]
            and report.get("custodyPublicKeySha256")
            == pinned["custodyPublicKeySha256"]
            == qualification["custodyPublicKeySha256"],
            "probe-key-identity",
        )
        require(report.get("softwareKeyExportable") is True, "probe-key-kind")
    else:
        require(report.get("keyChallengeProofSha256") is None, "probe-excess-key-proof")
    if role in ("primary", "witness"):
        require(
            report.get("postgresSystemId")
            == pinned["postgresSystemId"]
            == qualification[role + "SystemId"]
            and report.get("timeline")
            == pinned["timeline"]
            == qualification[role + "Timeline"],
            "probe-cluster-identity",
        )
        if role == "witness":
            require(
                report.get("witnessTipSequence") == qualification["witnessCutoff"]
                and report.get("witnessTipHash") == qualification["witnessCutoffHash"],
                "probe-witness-tip",
            )
    if role in ("archive", "checkpoint"):
        expected = qualification["custodyObjects"][role]
        require(
            report.get("objectId") == pinned["objectId"] == expected["objectId"]
            and report.get("objectSha256")
            == pinned["objectSha256"]
            == expected["sha256"]
            and report.get("objectBytes") == pinned["objectBytes"] == expected["bytes"],
            "probe-object-unrelated",
        )
        if role == "archive":
            final_objects = qualification.get("finalWalObjects")
            require(
                isinstance(final_objects, list)
                and 2 <= len(final_objects) <= 2000
                and report.get("finalWalObjects") == final_objects
                and report.get("finalWalObjectCount") == len(final_objects)
                and report.get("finalWalObjectSha256")
                == qualification.get("finalWalObjectSha256"),
                "probe-final-wal-incomplete",
            )
    return report


def evaluate(
    config,
    envelopes,
    nonce,
    qualification_sha,
    key_proof,
    qualification,
    live_transport,
):
    require(
        config.get("format") == "claimcore-deployment-1", "deployment-config-format"
    )
    require(
        set(config.get("roles", {})) == set(ROLES) and set(envelopes) == set(ROLES),
        "deployment-roles",
    )
    require(
        _pinned(nonce) and _pinned(qualification_sha) and _pinned(key_proof),
        "deployment-challenge",
    )
    reports = {
        role: _probe(
            config,
            role,
            envelopes[role],
            nonce,
            qualification_sha,
            key_proof,
            qualification,
        )
        for role in ROLES
    }
    require(
        reports["primary"]["postgresSystemId"]
        != reports["witness"]["postgresSystemId"],
        "clusters-not-independent",
    )
    for name in ("machineHash", "storageHash", "adminActorId", "hostKeyId"):
        require(
            len({report[name] for report in reports.values()}) == len(ROLES),
            "custody-not-independent",
        )
    require(
        config.get("mode") == "production" and live_transport,
        "local-only-qualification",
    )
    return {
        "status": "topology-evidence-corroborated",
        "realDataReady": False,
        "scope": "owner-pinned-probe-only",
    }
