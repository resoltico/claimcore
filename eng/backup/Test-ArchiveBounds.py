#!/usr/bin/env python3
"""Synthetic malformed tar negative controls for managed backup extraction."""

import importlib.util
import io
import tarfile
import tempfile
from pathlib import Path

path = Path(__file__).with_name("managed.py")
spec = importlib.util.spec_from_file_location("managed_backup_under_test", path)
managed = importlib.util.module_from_spec(spec)
spec.loader.exec_module(managed)


def refuses(name, size, entry_limit, byte_limit):
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
            managed.safe_extract(source, output, byte_limit, entry_limit)
        except managed.BackupFailure:
            return
        raise AssertionError("Hostile tar was accepted")


refuses("../escape", 1, 2, 1024)
refuses("safe", 2, 2, 1)
refuses("safe", 0, 1, 1024)
print("Unsafe member, expansion limit and entry-count limit refused.")
