#!/usr/bin/env python3
"""Synthetic malformed tar negative controls for managed backup extraction."""

import io
import sys
import tarfile
import tempfile
from pathlib import Path

sys.dont_write_bytecode = True
from managed_common import BackupFailureError
from managed_restore import safe_extract


def refuses(name: str, size: int, entry_limit: int, byte_limit: int) -> None:
    with tempfile.TemporaryDirectory(prefix="claimcore-malformed-tar-") as directory:
        root = Path(directory)
        source = root / "bad.tar"
        with tarfile.open(source, "w") as archive:
            for number in range(entry_limit + 1):
                member = tarfile.TarInfo(name if number == 0 else f"safe-{number}")
                member.size = size
                archive.addfile(member, io.BytesIO(b"x" * size))
        output = root / "extracted"
        output.mkdir()
        try:
            safe_extract(source, output, byte_limit, entry_limit)
        except BackupFailureError:
            return
        msg = "Hostile tar was accepted"
        raise AssertionError(msg)


refuses("../escape", 1, 2, 1024)
refuses("safe", 2, 2, 1)
refuses("safe", 0, 1, 1024)
sys.stdout.write("Unsafe member, expansion limit and entry-count limit refused." + "\n")
