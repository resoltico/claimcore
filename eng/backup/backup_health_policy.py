"""Reviewed backup-health policy and distinct independent custodian pins."""

import json
import re
from pathlib import Path

from backup_health_source_fields import POLICY_FIELDS, ROLE_FIELDS, _sha, _uuid
from deployment_common import canonical, private_path, require


def parse_policy(raw):
    require(isinstance(raw, bytes) and 0 < len(raw) <= 65536, "health-policy-size")
    value = json.loads(raw)
    require(
        isinstance(value, dict) and raw == canonical(value), "health-policy-canonical"
    )
    require(
        set(value) == POLICY_FIELDS
        and value["format"] == "claimcore-backup-health-policy-1",
        "health-policy-shape",
    )
    require(
        re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}", value["policyId"]),
        "health-policy-id",
    )
    for name in (
        "backupIntervalSeconds",
        "maximumBackupAgeSeconds",
        "maximumWalLagSeconds",
        "maximumCheckpointAgeSeconds",
        "maximumRestoreTestAgeSeconds",
        "restoreHorizonSeconds",
    ):
        require(
            type(value[name]) is int and 1 <= value[name] <= 365 * 86400,
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
    roots = [value[name] for name in ("archiveRoot", "checkpointRoot", "restoreRoot")]
    require(len(set(roots)) == 3, "health-policy-custody")
    for root in roots:
        require(Path(root).is_absolute(), "health-policy-custody")
        private_path(root, directory=True)
    roles = value["roles"]
    require(isinstance(roles, list) and len(roles) == 3, "health-policy-roles")
    require(
        {role.get("role") for role in roles}
        == {"archive", "checkpoint", "test-restore"},
        "health-policy-roles",
    )
    for name in (
        "publicKeyBase64",
        "machineSha256",
        "storageSha256",
        "adminActorId",
        "signerHolderActorId",
    ):
        values = []
        for role in roles:
            require(
                isinstance(role, dict) and set(role) == ROLE_FIELDS,
                "health-policy-role-shape",
            )
            item = role[name]
            require(
                _uuid(item)
                if name.endswith("ActorId")
                else (_sha(item) if name.endswith("Sha256") else isinstance(item, str)),
                "health-policy-role-pin",
            )
            values.append(item)
        require(len(set(values)) == 3, "health-policy-role-overlap")
    require(
        all(role["adminActorId"] != role["signerHolderActorId"] for role in roles),
        "health-policy-role-overlap",
    )
    return value
