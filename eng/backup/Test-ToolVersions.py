"""Synthetic negative controls for exact backup tool versions."""

import os
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

from tool_versions import POSTGRES_TOOLS, locate, matches


def main() -> None:
    for name in sorted(POSTGRES_TOOLS):
        expected = f"{name} (PostgreSQL) 18.6"
        assert matches(name, expected, expected)
        assert matches(name, expected, expected + " (Ubuntu 18.6-1.pgdg24.04+2)")
        assert matches(name, expected, expected + " (Debian 18.6-1.pgdg13+1)")
        for bad in (
            f"{name} (PostgreSQL) 18.5",
            f"{name} (PostgreSQL) 18.60",
            expected + " arbitrary-suffix",
            expected + " (Ubuntu 18.7-1)",
        ):
            assert not matches(name, expected, bad)
    assert matches("age", "v1.3.2", "v1.3.2\n")
    assert not matches("age", "v1.3.2", "v1.3.20")
    assert not matches("age", "v1.3.2", "v1.3.1")
    assert not matches("age", "v1.3.2", None)
    with tempfile.TemporaryDirectory(prefix="claimcore-pg-tool-") as temporary:
        selected = Path(temporary) / "pg_basebackup"
        selected.write_text("synthetic executable path only", encoding="utf-8")
        selected.chmod(0o700)
        with patch.dict(os.environ, {"CLAIMCORE_PG_BIN": temporary}):
            assert locate("pg_basebackup") == str(selected)
            assert locate("psql") is None
        with patch.dict(os.environ, {"CLAIMCORE_PG_BIN": "relative/path"}):
            assert locate("pg_basebackup") is None
    sys.stdout.write("Backup tool version boundaries passed." + "\n")


if __name__ == "__main__":
    main()
