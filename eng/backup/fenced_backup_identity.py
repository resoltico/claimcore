"""Read-only active writer identity for owner-held BASE capture."""

import re
import subprocess

import managed


def expected_identity(config):
    primary = managed.metadata(config, "primary")
    witness = managed.metadata(config, "witness")
    for index, name in enumerate(("installation", "lineage", "epoch")):
        managed.require(
            primary[index] == witness[index],
            "cluster-identity-mismatch-" + name,
        )
    result = subprocess.run(
        [
            managed.tool("psql", "psql (PostgreSQL) 18.6"),
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
        env=managed.pg_env(config["primary"]["metadataService"]),
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    managed.require(
        result.returncode == 0 and len(result.stdout) <= 32,
        "writer-generation-unavailable",
    )
    generation = result.stdout.decode("ascii", "strict").strip()
    managed.require(
        re.fullmatch(r"[1-9][0-9]{0,17}", generation), "writer-generation-invalid"
    )
    return {
        "installationId": primary[0],
        "lineageId": primary[1],
        "epoch": int(primary[2]),
        "writerGeneration": int(generation),
    }


def require_test_databases(config):
    for cluster in ("primary", "witness"):
        result = subprocess.run(
            [
                managed.tool("psql", "psql (PostgreSQL) 18.6"),
                "-XAtq",
                "-w",
                "-c",
                "SELECT current_database()",
            ],
            env=managed.pg_env(config[cluster]["metadataService"]),
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=10,
            check=False,
        )
        managed.require(
            result.returncode == 0 and len(result.stdout) <= 128,
            "synthetic-database-unavailable",
        )
        name = result.stdout.decode("ascii", "strict").strip()
        managed.require(name.endswith("_test"), "synthetic-database-name")
