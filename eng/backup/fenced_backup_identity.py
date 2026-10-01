"""Read-only active writer identity for owner-held BASE capture."""

import re
import subprocess

from backup_types import JsonObject
from managed_common import metadata, pg_env, require, tool

GENERATION_OUTPUT_LIMIT = 32
NAME_OUTPUT_LIMIT = 128


def expected_identity(config: JsonObject) -> JsonObject:
    """Read the active writer identity the held capture must match."""
    primary = metadata(config, "primary")
    witness = metadata(config, "witness")
    for index, name in enumerate(("installation", "lineage", "epoch")):
        require(
            primary[index] == witness[index],
            "cluster-identity-mismatch-" + name,
        )
    result = subprocess.run(
        [
            tool("psql", "psql (PostgreSQL) 18.6"),
            "-X",
            "-w",
            "-q",
            "-A",
            "-t",
            "-v",
            "ON_ERROR_STOP=1",
            "-c",
            "SELECT writer_generation::text FROM claimcore.installation_lineage WHERE singleton",
        ],
        env=pg_env(config["primary"]["metadataService"]),
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= GENERATION_OUTPUT_LIMIT,
        "writer-generation-unavailable",
    )
    generation = result.stdout.decode("ascii", "strict").strip()
    require(re.fullmatch(r"[1-9][0-9]{0,17}", generation), "writer-generation-invalid")
    return {
        "installationId": primary[0],
        "lineageId": primary[1],
        "epoch": int(primary[2]),
        "writerGeneration": int(generation),
    }


def require_test_databases(config: JsonObject) -> None:
    """Refuse synthetic capture against anything but disposable test databases."""
    for cluster in ("primary", "witness"):
        result = subprocess.run(
            [
                tool("psql", "psql (PostgreSQL) 18.6"),
                "-XAtq",
                "-w",
                "-c",
                "SELECT current_database()",
            ],
            env=pg_env(config[cluster]["metadataService"]),
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=10,
            check=False,
        )
        require(
            result.returncode == 0 and len(result.stdout) <= NAME_OUTPUT_LIMIT,
            "synthetic-database-unavailable",
        )
        name = result.stdout.decode("ascii", "strict").strip()
        require(name.endswith("_test"), "synthetic-database-name")
