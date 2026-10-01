"""Read a labelled disposable PostgreSQL system ID without database credentials."""

import re
import subprocess

from managed_common import refuse, require

CONTROLDATA_LIMIT = 8192


def system_id(container: str) -> str:
    """Read a disposable container's PostgreSQL system identifier."""
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
    require(
        result.returncode == 0 and len(result.stdout) <= CONTROLDATA_LIMIT,
        "synthetic-system-unavailable",
    )
    found = re.search(
        rb"^Database system identifier:\s*([1-9][0-9]{0,19})\s*$",
        result.stdout,
        re.MULTILINE,
    )
    if found is None:
        refuse("synthetic-system-invalid")
    return found.group(1).decode("ascii")
