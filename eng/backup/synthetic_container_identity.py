"""Read a labelled disposable PostgreSQL system ID without database credentials."""

import re
import subprocess

import managed


def system_id(container):
    result = subprocess.run(
        [
            "docker",
            "exec",
            "-u",
            "postgres",
            container,
            "pg_controldata",
            "/var/lib/postgresql/18/docker",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    managed.require(
        result.returncode == 0 and len(result.stdout) <= 8192,
        "synthetic-system-unavailable",
    )
    found = re.search(
        rb"^Database system identifier:\s*([1-9][0-9]{0,19})\s*$",
        result.stdout,
        re.MULTILINE,
    )
    managed.require(found is not None, "synthetic-system-invalid")
    return found.group(1).decode("ascii")
