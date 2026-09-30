"""Reviewed backup-health policy and distinct independent custodian pins."""

import json
import re
from pathlib import Path

from backup_health_source_fields import POLICY_FIELDS, ROLE_FIELDS
from backup_types import JsonObject
from deployment_common import canonical, is_sha256, is_uuid, private_path, require

POLICY_LIMIT = 65536
MAX_POLICY_SECONDS = 365 * 86400
AGES = (
    "backupIntervalSeconds",
    "maximumBackupAgeSeconds",
    "maximumWalLagSeconds",
    "maximumCheckpointAgeSeconds",
    "maximumRestoreTestAgeSeconds",
    "restoreHorizonSeconds",
)
ROLE_NAMES = {"archive", "checkpoint", "test-restore"}
ROLE_PINS = (
    "publicKeyBase64",
    "machineSha256",
    "storageSha256",
    "adminActorId",
    "signerHolderActorId",
)


def _check_ages(value: JsonObject) -> None:
    for name in AGES:
        require(
            type(value[name]) is int and 1 <= value[name] <= MAX_POLICY_SECONDS,
            "health-policy-age",
        )
    require(
        value["backupIntervalSeconds"]
        <= value["maximumBackupAgeSeconds"]
        <= value["restoreHorizonSeconds"],
        "health-policy-age",
    )
    for name in (
        "maximumWalLagSeconds",
        "maximumCheckpointAgeSeconds",
        "maximumRestoreTestAgeSeconds",
    ):
        require(value[name] <= value["restoreHorizonSeconds"], "health-policy-age")


def _check_roots(value: JsonObject) -> None:
    roots = [value[name] for name in ("archiveRoot", "checkpointRoot", "restoreRoot")]
    require(len(set(roots)) == len(roots), "health-policy-custody")
    for root in roots:
        require(Path(root).is_absolute(), "health-policy-custody")
        private_path(root, directory=True)


def _check_roles(roles: list[JsonObject]) -> None:
    require(isinstance(roles, list) and len(roles) == len(ROLE_NAMES), "health-policy-roles")
    require({role.get("role") for role in roles} == ROLE_NAMES, "health-policy-roles")
    for name in ROLE_PINS:
        values = []
        for role in roles:
            require(isinstance(role, dict) and set(role) == ROLE_FIELDS, "health-policy-role-shape")
            item = role[name]
            require(
                is_uuid(item)
                if name.endswith("ActorId")
                else (is_sha256(item) if name.endswith("Sha256") else isinstance(item, str)),
                "health-policy-role-pin",
            )
            values.append(item)
        require(len(set(values)) == len(roles), "health-policy-role-overlap")
    require(
        all(role["adminActorId"] != role["signerHolderActorId"] for role in roles),
        "health-policy-role-overlap",
    )


def parse_policy(raw: bytes) -> JsonObject:
    """Parse the canonical health policy, refusing overlapping custody or pins."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= POLICY_LIMIT, "health-policy-size")
    value: JsonObject = json.loads(raw)
    require(isinstance(value, dict) and raw == canonical(value), "health-policy-canonical")
    require(
        set(value) == POLICY_FIELDS and value["format"] == "claimcore-backup-health-policy-1",
        "health-policy-shape",
    )
    require(
        re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}", value["policyId"]), "health-policy-id"
    )
    _check_ages(value)
    _check_roots(value)
    _check_roles(value["roles"])
    return value
