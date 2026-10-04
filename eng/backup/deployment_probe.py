#!/usr/bin/env python3
"""On-host, owner-private challenge probe; never grants deployment admission itself."""

import argparse
import base64
import hashlib
import json
import os
import platform
import re
import subprocess
import sys
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from types import TracebackType

from backup_types import JsonObject

sys.dont_write_bytecode = True
from deployment_common import (
    DeploymentRefusalError,
    is_sha256,
    is_uuid,
    private_path,
    require,
    sign,
    utc,
)
from deployment_final_wal import read_final_wal
from deployment_topology import ROLES

CHALLENGE_LIMIT = 4096
DATABASE_TIMEOUT_SECONDS = 10
DATABASE_OUTPUT_LIMIT = 512
KEY_SECRET_BYTES = 32
PROBE_SECONDS = 60
CGROUP_LIMIT = 8192
MIN_PYTHON = (3, 12)
PRIMARY_FIELDS = 5
WITNESS_FIELDS = 7
PRIMARY_QUERY = (
    "SELECT l.installation_id::text,l.lineage_id::text,l.witness_epoch::text,"
    "s.system_identifier::text,c.timeline_id::text "
    "FROM claimcore.installation_lineage l CROSS JOIN pg_control_system() s "
    "CROSS JOIN pg_control_checkpoint() c WHERE l.singleton"
)
WITNESS_QUERY = (
    "SELECT l.installation_id::text,l.lineage_id::text,l.epoch::text,"
    "s.system_identifier::text,c.timeline_id::text,l.tip_sequence::text,"
    "encode(l.tip_hash,'hex') FROM claimcore_witness.installation l "
    "CROSS JOIN pg_control_system() s CROSS JOIN pg_control_checkpoint() c "
    "WHERE l.singleton"
)

type Availability = tuple[str, bool, str | None, JsonObject]


def machine_id() -> bytes:
    """Return the host's stable machine identifier, refusing anything unreadable or all-zero."""
    system = platform.system()
    if system == "Linux":
        try:
            value = Path("/etc/machine-id").read_text("ascii").strip().lower()
        except (OSError, UnicodeError):
            msg = "machine-id-unavailable"
            raise DeploymentRefusalError(msg) from None
        require(
            re.fullmatch(r"[0-9a-f]{32}", value) is not None and value != "0" * 32,
            "machine-id-unavailable",
        )
        return value.encode("ascii")
    if system == "Darwin":
        result = subprocess.run(
            ["ioreg", "-rd1", "-c", "IOPlatformExpertDevice"],
            capture_output=True,
            check=False,
        )
        require(result.returncode == 0, "machine-id-unavailable")
        found = re.search(rb'"IOPlatformUUID" = "([0-9A-Fa-f-]{36})"', result.stdout)
        if found is None:
            msg = "machine-id-unavailable"
            raise DeploymentRefusalError(msg)
        return found.group(1).lower()
    msg = "host-platform-unsupported"
    raise DeploymentRefusalError(msg)


def in_container() -> bool:
    """Whether this process runs inside a container."""
    if Path("/.dockerenv").exists():
        return True
    for source in ("/proc/1/cgroup", "/proc/self/cgroup"):
        path = Path(source)
        if path.exists():
            body = path.read_text("ascii", errors="replace")[:CGROUP_LIMIT]
            if any(marker in body for marker in ("docker", "kubepods", "containerd")):
                return True
    return False


def _database_fields(config: JsonObject, role: str) -> list[str]:
    service = config["metadataService"]
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,63}", service) is not None, "service-name")
    environment = os.environ.copy()
    environment["PGSERVICE"] = service
    environment["PGCONNECT_TIMEOUT"] = "5"
    query = PRIMARY_QUERY if role == "primary" else WITNESS_QUERY
    result = subprocess.run(
        ["psql", "-X", "-w", "-q", "-A", "-t", "-F", "\t", "-v", "ON_ERROR_STOP=1", "-c", query],
        env=environment,
        capture_output=True,
        timeout=DATABASE_TIMEOUT_SECONDS,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= DATABASE_OUTPUT_LIMIT,
        "database-evidence-unavailable",
    )
    fields = result.stdout.decode("ascii", "strict").strip().split("\t")
    expected = PRIMARY_FIELDS if role == "primary" else WITNESS_FIELDS
    require(len(fields) == expected, "database-evidence-shape")
    return fields


def _database_evidence(config: JsonObject, role: str) -> Availability:
    fields = _database_fields(config, role)
    for identifier in fields[:2]:
        require(str(uuid.UUID(identifier)) == identifier, "database-identity")
    epoch, system_id, timeline = int(fields[2]), fields[3], int(fields[4])
    require(
        epoch > 0 and re.fullmatch(r"[0-9]{1,20}", system_id) is not None and timeline > 0,
        "database-evidence-shape",
    )
    evidence: JsonObject = {
        "installationId": fields[0],
        "lineageId": fields[1],
        "epoch": epoch,
        "postgresSystemId": system_id,
        "timeline": timeline,
        "witnessTipSequence": None,
        "witnessTipHash": None,
    }
    if role == "witness":
        tip = int(fields[5])
        require(tip >= 0 and is_sha256(fields[6]), "witness-tip-shape")
        evidence.update(witnessTipSequence=tip, witnessTipHash=fields[6])
    return "database-read", True, None, evidence


def _retained_copy(config: JsonObject, role: str) -> Availability:
    copy = private_path(config["availabilityFile"])
    expected = config["objectSha256"]
    require(is_sha256(expected), "availability-digest")
    require(is_uuid(config["objectId"]), "managed-object-id")
    expected_bytes = config["objectBytes"]
    require(type(expected_bytes) is int and expected_bytes > 0, "managed-object-size")
    with copy.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    evidence: JsonObject = {
        "objectId": config["objectId"],
        "objectSha256": actual,
        "objectBytes": copy.stat().st_size,
    }
    if role == "archive":
        evidence.update(read_final_wal(config))
    return (
        "retained-copy",
        actual == expected and evidence["objectBytes"] == expected_bytes,
        None,
        evidence,
    )


def _key_possession(config: JsonObject, challenge: str | None) -> Availability:
    require(challenge is not None and len(challenge) <= CHALLENGE_LIMIT, "key-challenge-missing")
    encrypted = base64.b64decode(str(challenge), validate=True)
    identity = private_path(config["ageIdentity"])
    result = subprocess.run(
        ["age", "--decrypt", "--identity", str(identity)],
        input=encrypted,
        capture_output=True,
        timeout=DATABASE_TIMEOUT_SECONDS,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) == KEY_SECRET_BYTES, "key-challenge-failed"
    )
    require(is_uuid(config["custodyKeyId"]), "custody-key-id")
    recipient = config["ageRecipient"]
    require(
        re.fullmatch(r"age1[023456789acdefghjklmnpqrstuvwxyz]{58}", recipient) is not None,
        "key-recipient",
    )
    evidence: JsonObject = {
        "custodyKeyId": config["custodyKeyId"],
        "custodyPublicKeySha256": hashlib.sha256(recipient.encode("ascii")).hexdigest(),
        "softwareKeyExportable": True,
    }
    return "key-possession", True, hashlib.sha256(result.stdout).hexdigest(), evidence


def availability(config: JsonObject, role: str, challenge: str | None) -> Availability:
    """Collect the role-specific evidence: database, retained copy or key possession."""
    if role in ("primary", "witness"):
        return _database_evidence(config, role)
    if role in ("archive", "checkpoint"):
        return _retained_copy(config, role)
    return _key_possession(config, challenge)


def _host_hashes(config: JsonObject) -> tuple[str, str]:
    machine_hash = hashlib.sha256(b"claimcore-deployment-host-v1\0" + machine_id()).hexdigest()
    storage = private_path(config["storagePath"], directory=True, owner=False)
    storage_hash = hashlib.sha256(
        b"claimcore-deployment-storage-v1\0"
        + bytes.fromhex(machine_hash)
        + str(storage.stat().st_dev).encode("ascii")
    ).hexdigest()
    return machine_hash, storage_hash


def probe(
    config: JsonObject, role: str, nonce: str, report_sha: str, challenge: str | None
) -> JsonObject:
    """Answer one challenge with a signed report about this host and its role evidence."""
    require(config.get("format") == "claimcore-deployment-probe-config-1", "probe-config-format")
    require(role in ROLES and role == config.get("role"), "probe-role")
    require(is_sha256(nonce), "probe-nonce")
    require(is_sha256(report_sha), "qualification-digest")
    for name in ("adminActorId", "hostKeyId"):
        require(is_uuid(config[name]), "probe-identity")
    machine_hash, storage_hash = _host_hashes(config)
    kind, available, key_proof, evidence = availability(config, role, challenge)
    if role not in ("primary", "witness"):
        evidence.update(
            installationId=config["installationId"],
            lineageId=config["lineageId"],
            epoch=config["epoch"],
        )
    now = datetime.now(UTC)
    report = {
        "format": "claimcore-deployment-probe-1",
        "role": role,
        "nonce": nonce,
        "qualificationSha256": report_sha,
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(seconds=PROBE_SECONDS)),
        "machineHash": machine_hash,
        "storageHash": storage_hash,
        "adminActorId": config["adminActorId"],
        "hostKeyId": config["hostKeyId"],
        "containerized": in_container(),
        "availabilityKind": kind,
        "available": available,
        "keyChallengeProofSha256": key_proof,
        **evidence,
    }
    return sign(report, config["probeSigningKey"])


def main() -> None:
    """Answer one deployment challenge and write the signed report."""
    os.umask(0o077)
    require(sys.version_info >= MIN_PYTHON, "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--role", choices=ROLES, required=True)
    parser.add_argument("--nonce", required=True)
    parser.add_argument("--qualification-sha256", required=True)
    parser.add_argument("--key-challenge")
    args = parser.parse_args()
    source = os.environ.get("CLAIMCORE_DEPLOY_PROBE_CONFIG")
    require(source is not None, "probe-config-missing")
    config = json.loads(private_path(str(source)).read_bytes())
    envelope = probe(config, args.role, args.nonce, args.qualification_sha256, args.key_challenge)
    sys.stdout.write(json.dumps(envelope, sort_keys=True, separators=(",", ":")) + "\n")


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    category = error.args[0] if isinstance(error, DeploymentRefusalError) else "probe-failed"
    sys.stderr.write(json.dumps({"status": "refused", "reason": category}) + "\n")


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()
