"""Closed version admission for owner backup command-line dependencies."""

import re

POSTGRES_TOOLS = frozenset(
    {"psql", "pg_isready", "pg_verifybackup", "pg_waldump", "pg_basebackup"}
)


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
