"""Boot a recovered base backup in an isolated, network-less container and read its identity."""

import json
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path

from backup_types import JsonObject
from managed_copy_source import run_fixed
from managed_copy_verification_io import VerificationFailureError, require, tool

ROOT = Path(__file__).resolve().parents[2]
PGDATA = "/var/lib/postgresql/18/docker"
DOCKER_TIMEOUT_SECONDS = 120
COPY_TIMEOUT_SECONDS = 180
STOP_TIMEOUT_SECONDS = 30
IDENTITY_PARTS = 3
DOCKER_OPERATIONS = ("mkdir", "cp", "chown", "chmod", "pg_ctl", "psql")
PRIMARY_IDENTITY = (
    "SELECT installation_id::text || ':' || lineage_id::text || ':' || "
    "witness_epoch::text FROM claimcore.installation_lineage WHERE singleton"
)
WITNESS_IDENTITY = (
    "SELECT installation_id::text || ':' || lineage_id::text || ':' || "
    "epoch::text FROM claimcore_witness.installation WHERE singleton"
)


@dataclass(frozen=True)
class Recovered:
    """A running recovered cluster and the owner role and database to query it with."""

    container: str
    role: str
    database: str


def _image() -> str:
    with (ROOT / "db/postgresql-baseline.json").open("rb") as stream:
        image = json.load(stream)["containerImage"]
    require(isinstance(image, str) and "@sha256:" in image, "POSTGRES_IMAGE_UNPINNED")
    return str(image)


def _docker(args: list[str], timeout: int = DOCKER_TIMEOUT_SECONDS) -> str:
    try:
        return run_fixed([tool("docker", None), *args], timeout)
    except VerificationFailureError:
        operation = next((piece for piece in args if piece in DOCKER_OPERATIONS), args[0])
        raise VerificationFailureError("DOCKER_" + operation.upper() + "_REFUSED") from None


def _query(target: Recovered, sql: str) -> str:
    return _docker(
        [
            "exec",
            "-u",
            "postgres",
            target.container,
            "psql",
            "-X",
            "-A",
            "-t",
            "-h",
            "/var/run/postgresql",
            "-p",
            "5432",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            target.role,
            "-d",
            target.database,
            "-c",
            sql,
        ]
    )


def _start(image: str) -> str:
    identifier = _docker(
        [
            "run",
            "--rm",
            "-d",
            "--network",
            "none",
            "--label",
            "org.claimcore.copy-verification=synthetic-owner",
            "--entrypoint",
            "sleep",
            image,
            "900",
        ]
    )
    require(re.fullmatch(r"[0-9a-f]{64}", identifier) is not None, "ISOLATED_CONTAINER_REFUSED")
    return identifier


def _load(identifier: str, data: Path) -> None:
    _docker(["exec", "-u", "root", identifier, "mkdir", "-p", PGDATA])
    _docker(["cp", str(data) + "/.", identifier + ":" + PGDATA], COPY_TIMEOUT_SECONDS)
    _docker(
        ["exec", "-u", "root", identifier, "chown", "-R", "postgres:postgres", PGDATA],
        COPY_TIMEOUT_SECONDS,
    )
    _docker(["exec", "-u", "root", identifier, "chmod", "700", PGDATA])
    _docker(
        [
            "exec",
            "-u",
            "postgres",
            identifier,
            "pg_ctl",
            "-D",
            PGDATA,
            "-l",
            PGDATA + "/copy-verification.log",
            "start",
        ],
        DOCKER_TIMEOUT_SECONDS,
    )


def _check_identity(config: JsonObject, target: Recovered) -> None:
    system = _query(target, "SELECT system_identifier::text FROM pg_control_system()")
    require(system == config["postgresSystemId"], "RECOVERED_SYSTEM_DIVERGED")
    primary = config["cluster"] == "PRIMARY"
    recovered = _query(target, PRIMARY_IDENTITY if primary else WITNESS_IDENTITY).split(":")
    require(len(recovered) == IDENTITY_PARTS, "RECOVERED_IDENTITY_DIVERGED")
    require(
        re.fullmatch(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", recovered[0])
        is not None,
        "RECOVERED_INSTALLATION_FORMAT_DIVERGED",
    )
    require(recovered[0] == config["installationId"], "RECOVERED_INSTALLATION_DIVERGED")
    require(recovered[1] == config["lineageId"], "RECOVERED_LINEAGE_DIVERGED")
    require(recovered[2] == str(config["witnessEpoch"]), "RECOVERED_EPOCH_DIVERGED")


def _row_count(config: JsonObject, target: Recovered) -> int:
    timeline = _query(target, "SELECT timeline_id::bigint FROM pg_control_checkpoint()")
    count_query = (
        "SELECT count(*) FROM claimcore.case_changes"
        if config["cluster"] == "PRIMARY"
        else "SELECT count(*) FROM claimcore_witness.journal"
    )
    count = _query(target, count_query)
    system = _query(target, "SELECT system_identifier::text FROM pg_control_system()")
    require(
        system == config["postgresSystemId"]
        and int(timeline) == config["timeline"]
        and count.isascii()
        and count.isdigit(),
        "RECOVERED_DATA_DIVERGED",
    )
    return int(count)


def boot_identity(config: JsonObject, data: Path) -> int:
    """Boot the recovered data, prove its identity and timeline, and return its row count."""
    identifier = _start(_image())
    try:
        _load(identifier, data)
        target = Recovered(identifier, config["databaseOwnerRole"], config["databaseName"])
        _check_identity(config, target)
        return _row_count(config, target)
    finally:
        subprocess.run(
            [tool("docker", None), "stop", identifier],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=STOP_TIMEOUT_SECONDS,
            check=False,
        )
