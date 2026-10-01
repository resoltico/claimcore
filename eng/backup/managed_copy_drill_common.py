"""Process and file helpers shared by the physical-copy verifier drill."""

import hashlib
import json
import os
import re
import subprocess
from collections.abc import Sequence
from pathlib import Path

from backup_types import Json

VERIFIER = Path(__file__).resolve().parent / "Verify-ManagedCopy.sh"
PROCESS_TIMEOUT_SECONDS = 240
REASON_PATTERN = re.compile(r"[A-Z_]{1,70}")


def execute(args: Sequence[str]) -> subprocess.CompletedProcess[str]:
    """Run one fixed argument vector with captured text output."""
    return subprocess.run(
        list(args),
        capture_output=True,
        text=True,
        timeout=PROCESS_TIMEOUT_SECONDS,
        check=False,
    )


def run(args: Sequence[str], expected: int = 0) -> str:
    """Run `args`, requiring the exit code `expected`, and return its output."""
    result = execute(args)
    if result.returncode != expected:
        msg = "managed-copy-verifier-test-refused"
        raise RuntimeError(msg)
    return result.stdout


def private_json(path: Path | str, value: Json) -> None:
    """Create an owner-only JSON file that must not already exist."""
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "w", encoding="ascii") as stream:
        json.dump(value, stream, sort_keys=True, separators=(",", ":"))
        stream.write("\n")


def digest(path: Path) -> str:
    """Return the SHA-256 of a file."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def refusal_reason(stderr: str, default: str) -> str:
    """Return the typed refusal reason in `stderr`, or `default` when it is not typed JSON."""
    try:
        return str(json.loads(stderr).get("reason", default))
    except (ValueError, TypeError, AttributeError):
        return default


def stage_name(reason: str) -> str:
    """Reduce a reason to the lowercase-and-hyphen alphabet a stage name may use."""
    return re.sub(r"[^a-z-]", "", reason.lower().replace("_", "-"))
