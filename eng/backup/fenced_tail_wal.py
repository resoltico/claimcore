"""WAL segment arithmetic and the pinned pg_waldump for synthetic fenced-tail capture."""

import sys
from pathlib import Path

from backup_types import Json
from fenced_tail_io import LSN, SEGMENT, CaptureRefusalError, command, require
from tool_versions import matches as matches_tool_version

LOG_SHIFT = 32
TIMELINE_DIGITS = 8
LOG_END = 16
VERSION_OUTPUT_LIMIT = 64


def waldump_executable() -> str:
    """Return the code-owned pg_waldump path after proving its exact pinned version."""
    if sys.platform == "darwin":
        candidates = ("/opt/homebrew/opt/libpq/bin/pg_waldump",)
    elif sys.platform.startswith("linux"):
        candidates = ("/usr/lib/postgresql/18/bin/pg_waldump", "/usr/bin/pg_waldump")
    else:
        msg = "wal-inspection-tool-unavailable"
        raise CaptureRefusalError(msg)
    for candidate in candidates:
        if Path(candidate).is_file():
            version = command(
                [candidate, "--version"], maximum=VERSION_OUTPUT_LIMIT, stage="wal-inspection-tool"
            )
            require(
                matches_tool_version("pg_waldump", "pg_waldump (PostgreSQL) 18.6", version),
                "wal-inspection-version",
            )
            return candidate
    msg = "wal-inspection-tool-unavailable"
    raise CaptureRefusalError(msg)


def lsn_number(value: Json) -> int:
    """Return the numeric position of a PostgreSQL LSN."""
    require(isinstance(value, str) and LSN.fullmatch(value), "wal-lsn")
    high, low = value.split("/")
    return (int(high, 16) << LOG_SHIFT) + int(low, 16)


def _per_log(segment_bytes: int) -> int:
    require(segment_bytes > 0 and (1 << LOG_SHIFT) % segment_bytes == 0, "wal-segment-size")
    return (1 << LOG_SHIFT) // segment_bytes


def segment_number(value: Json, segment_bytes: int, timeline: int) -> int:
    """Return the absolute number of a WAL segment on `timeline`."""
    require(isinstance(value, str) and SEGMENT.fullmatch(value), "wal-segment")
    require(int(value[:TIMELINE_DIGITS], 16) == timeline, "wal-timeline")
    per_log = _per_log(segment_bytes)
    index = int(value[LOG_END:], 16)
    require(index < per_log, "wal-segment")
    return int(value[TIMELINE_DIGITS:LOG_END], 16) * per_log + index


def segments_between(
    first: str, last: str, segment_bytes: int, timeline: int, limit: int
) -> list[str]:
    """List every segment from `first` through `last`, refusing a range of `limit` or more."""
    start = segment_number(first, segment_bytes, timeline)
    end = segment_number(last, segment_bytes, timeline)
    require(start <= end and end - start < limit, "wal-range")
    per_log = _per_log(segment_bytes)
    prefix = f"{timeline:08X}"
    return [
        f"{prefix}{number // per_log:08X}{number % per_log:08X}" for number in range(start, end + 1)
    ]


def first_segment_for_horizon(horizon: Json, segment_bytes: int, timeline: int) -> str:
    """Return the segment that holds the registered WAL horizon."""
    per_log = _per_log(segment_bytes)
    number = lsn_number(horizon) // segment_bytes
    return f"{timeline:08X}{number // per_log:08X}{number % per_log:08X}"
