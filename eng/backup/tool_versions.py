"""Closed version admission for owner backup command-line dependencies."""

import os
import re
import shutil

POSTGRES_TOOLS = frozenset(
    {"psql", "pg_isready", "pg_verifybackup", "pg_waldump", "pg_basebackup"}
)


def locate(name):
    selected = os.environ.get("CLAIMCORE_PG_BIN")
    if name in POSTGRES_TOOLS and selected is not None:
        if not os.path.isabs(selected):
            return None
        candidate = os.path.join(selected, name)
        return (
            candidate
            if os.path.isfile(candidate) and os.access(candidate, os.X_OK)
            else None
        )
    return shutil.which(name)


def matches(name, expected, observed):
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
