"""Isolated exact libpq profile and remote TLS policy checks."""

import hashlib
import os
import sys
import tempfile
from pathlib import Path

from backup_types import JsonObject
from deployment_common import DeploymentRefusalError
from managed_common import pg_env
from pg_service_policy import validate


def profile(host: str, database: str | None = None, tls: str = "") -> str:
    values = f"host={host}\nport=5432\nuser=backup_role\npassword=test-only\n"
    if database is not None:
        values += f"dbname={database}\n"
    return values + tls


def expect_refusal(config: JsonObject, reason: str) -> None:
    try:
        validate(config)
    except DeploymentRefusalError as failure:
        assert str(failure) == reason, (str(failure), reason)
    else:
        msg = f"expected {reason}"
        raise AssertionError(msg)


with tempfile.TemporaryDirectory(
    prefix="claimcore-pgservice-test-", dir="/private/tmp"
) as directory:
    root = Path(directory)
    root.chmod(0o700)
    source = root / "pg_service.conf"
    names = {
        "primary": {
            "metadataService": "primary_owner",
            "replicationService": "primary_backup",
        },
        "witness": {
            "metadataService": "witness_owner",
            "replicationService": "witness_backup",
        },
    }
    text = "".join(
        f"[{name}]\n{profile('127.0.0.1', database)}"
        for name, database in (
            ("primary_owner", "primary_test"),
            ("primary_backup", None),
            ("witness_owner", "witness_test"),
            ("witness_backup", None),
        )
    )
    source.write_text(text, encoding="utf-8")
    source.chmod(0o600)
    config = {
        **names,
        "pgServiceFile": str(source),
        "pgServiceFileSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "pgTlsRootSha256": None,
        "checkpointSignerMode": "LOCAL_SYNTHETIC",
    }
    previous = os.environ.pop("PGSERVICEFILE", None)
    try:
        assert validate(config) == source
        source.write_text("[DEFAULT]\nhost=127.0.0.1\n" + text, encoding="utf-8")
        inherited = {
            **config,
            "pgServiceFileSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        }
        expect_refusal(inherited, "pgservice-defaults")
        source.write_text(text, encoding="utf-8")
        os.environ["PGSSLMODE"] = "disable"
        os.environ["PGREQUIREAUTH"] = "none"
        child = pg_env("primary_owner")
        assert child["PGSERVICEFILE"] == str(source)
        assert child["PGSERVICE"] == "primary_owner"
        assert "PGSSLMODE" not in child and "PGREQUIREAUTH" not in child
        os.environ.pop("PGSSLMODE")
        os.environ.pop("PGREQUIREAUTH")
        expect_refusal({**config, "pgServiceFileSha256": "0" * 64}, "pgservice-digest")
        os.environ["PGSERVICEFILE"] = str(root / "other.conf")
        expect_refusal(config, "pgservice-ambient-mismatch")
        os.environ.pop("PGSERVICEFILE")
        ca = root / "root-ca.pem"
        ca.write_text("synthetic CA marker\n", encoding="ascii")
        ca.chmod(0o600)
        tls = f"sslmode=verify-full\nsslrootcert={ca}\n"
        remote_text = "".join(
            f"[{name}]\n{profile(host, database, tls)}"
            for name, host, database in (
                ("primary_owner", "primary.example.invalid", "primary"),
                ("primary_backup", "primary.example.invalid", None),
                ("witness_owner", "witness.example.invalid", "witness"),
                ("witness_backup", "witness.example.invalid", None),
            )
        )
        source.write_text(remote_text, encoding="utf-8")
        remote = {
            **config,
            "checkpointSignerMode": "REMOTE_SSH",
            "pgServiceFileSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "pgTlsRootSha256": {
                "primary": hashlib.sha256(ca.read_bytes()).hexdigest(),
                "witness": hashlib.sha256(ca.read_bytes()).hexdigest(),
            },
        }
        assert validate(remote) == source
        expect_refusal(
            {
                **remote,
                "pgTlsRootSha256": {
                    "primary": "0" * 64,
                    "witness": hashlib.sha256(ca.read_bytes()).hexdigest(),
                },
            },
            "pgservice-ca-pin",
        )
        source.write_text(
            remote_text.replace("sslmode=verify-full", "sslmode=require"),
            encoding="utf-8",
        )
        remote["pgServiceFileSha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
        expect_refusal(remote, "pgservice-tls-required")
    finally:
        if previous is None:
            os.environ.pop("PGSERVICEFILE", None)
        else:
            os.environ["PGSERVICEFILE"] = previous

sys.stdout.write("pg-service-policy=passed" + "\n")
