"""Closed version admission for owner backup command-line dependencies."""

import os
import re
import shutil
from pathlib import Path

POSTGRES_TOOLS = frozenset({"psql", "pg_isready", "pg_verifybackup", "pg_waldump", "pg_basebackup"})


def locate(name: str) -> str | None:
    """Return the executable for `name`, honouring the selected PostgreSQL directory."""
    selected = os.environ.get("CLAIMCORE_PG_BIN")
    if name in POSTGRES_TOOLS and selected is not None:
        directory = Path(selected)
        if not directory.is_absolute():
            return None
        candidate = directory / name
        return str(candidate) if candidate.is_file() and os.access(candidate, os.X_OK) else None
    return shutil.which(name)


def matches(name: str, expected: str, observed: object) -> bool:
    """Whether `observed` is the exact admitted version output for tool `name`."""
    if not isinstance(observed, str):
        return False
    value = observed.strip()
    if name in POSTGRES_TOOLS:
        if expected != f"{name} (PostgreSQL) 18.6":
            return False
        return (
            re.fullmatch(
                rf"{re.escape(name)} \(PostgreSQL\) 18\.6"
                r"(?: \((?:Ubuntu|Debian) 18\.6-[0-9A-Za-z.+~_-]+\))?",
                value,
            )
            is not None
        )
    return value == expected
