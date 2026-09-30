#!/usr/bin/env python3
"""Pure refusal tests for finite, same-timeline fenced WAL capture."""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.dont_write_bytecode = True
import fenced_tail_capture as tail
import fenced_tail_wal as wal
from fenced_tail_io import (
    command,
    create_private_json,
    private_file,
    regular_segment,
    require_container_segment,
)


class FencedRecoveryTailTests(unittest.TestCase):
    def test_cluster_queries_use_the_exact_owner_role(self) -> None:
        with patch.object(tail, "command", return_value="1") as execute:
            tail.cluster_query("0" * 64, "cc_owner", "SELECT 1", stage="cluster-identity")
            arguments = execute.call_args.args[0]
            self.assertEqual(arguments[arguments.index("-U") + 1], "cc_owner")

    def test_waldump_uses_code_owned_path_and_exact_pinned_version(self) -> None:
        with (
            patch("fenced_tail_wal.sys.platform", "darwin"),
            patch("fenced_tail_wal.Path.is_file", return_value=True),
            patch.object(wal, "command", return_value="pg_waldump (PostgreSQL) 18.6") as execute,
        ):
            self.assertEqual(wal.waldump_executable(), "/opt/homebrew/opt/libpq/bin/pg_waldump")
            self.assertEqual(execute.call_args.args[0][-1], "--version")
            execute.return_value = "pg_waldump (PostgreSQL) 17.9"
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-inspection-version"):
                wal.waldump_executable()

    def test_missing_container_segment_is_distinct_from_copy_failure(self) -> None:
        with patch("fenced_tail_io.subprocess.run") as run:
            run.return_value = subprocess.CompletedProcess([], 1, b"", b"private")
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-segment-missing"):
                require_container_segment("0" * 64, "/pg_wal/000000010000000000000001")
            run.return_value = subprocess.CompletedProcess([], 0, b"", b"")
            require_container_segment("0" * 64, "/pg_wal/000000010000000000000001")

    def test_subprocess_failures_have_fixed_stage_codes(self) -> None:
        with patch("fenced_tail_io.subprocess.run") as run:
            run.return_value = subprocess.CompletedProcess([], 1, b"", b"private detail")
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-segment-copy-exit"):
                command(["docker", "cp"], maximum=0, stage="wal-segment-copy")
            run.return_value = subprocess.CompletedProcess([], 0, b"progress", b"")
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-segment-copy-stdout-bound"):
                command(["docker", "cp"], maximum=0, stage="wal-segment-copy")
            run.side_effect = OSError("private executable path")
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-inspection-unavailable"):
                command(["pg_waldump"], stage="wal-inspection")

    def test_segment_range_is_contiguous_across_log_boundary(self) -> None:
        names = wal.segments_between(
            "0000000100000000000000FF",
            "000000010000000100000001",
            16 * 1024 * 1024,
            1,
            4,
        )
        self.assertEqual(
            names,
            [
                "0000000100000000000000FF",
                "000000010000000100000000",
                "000000010000000100000001",
            ],
        )

    def test_wrong_timeline_and_gap_limit_refuse(self) -> None:
        with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-timeline"):
            wal.segments_between(
                "000000020000000000000001",
                "000000020000000000000002",
                16 * 1024 * 1024,
                1,
                4,
            )
        with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-range"):
            wal.segments_between(
                "000000010000000000000001",
                "000000010000000000000004",
                16 * 1024 * 1024,
                1,
                3,
            )

    def test_switch_uses_server_endpoint_and_rejects_no_progress(self) -> None:
        results = [
            "0/100",
            "0/101|000000010000000000000000",
        ]
        with patch.object(tail, "cluster_query", side_effect=results):
            self.assertEqual(
                tail.switch_range("0" * 64, "owner", "0/100", 16 * 1024 * 1024, 1),
                ("000000010000000000000000", "000000010000000000000000", "0/101"),
            )
        results[-1] = "0/100|000000010000000000000000"
        with (
            patch.object(tail, "cluster_query", side_effect=results),
            self.assertRaisesRegex(tail.CaptureRefusalError, "wal-switch-no-progress"),
        ):
            tail.switch_range("0" * 64, "owner", "0/100", 16 * 1024 * 1024, 1)

    def test_exact_segment_boundary_starts_new_segment(self) -> None:
        self.assertEqual(
            wal.first_segment_for_horizon("0/1000000", 16 * 1024 * 1024, 1),
            "000000010000000000000001",
        )
        self.assertEqual(
            wal.first_segment_for_horizon("0/FFFFFF", 16 * 1024 * 1024, 1),
            "000000010000000000000000",
        )
        start = wal.first_segment_for_horizon("0/1000000", 16 * 1024 * 1024, 1)
        self.assertEqual(
            wal.segments_between(start, start, 16 * 1024 * 1024, 1, 1),
            ["000000010000000000000001"],
        )

    def test_missing_required_next_segment_refuses_even_if_predecessor_exists(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            predecessor = root / "000000010000000000000000"
            predecessor.write_bytes(b"synthetic predecessor")
            required = root / "000000010000000000000001"
            with self.assertRaisesRegex(tail.CaptureRefusalError, "wal-segment-missing"):
                regular_segment(required, 16 * 1024 * 1024)

    def test_output_is_create_only_canonical_and_private(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "metadata.json"
            create_private_json(path, {"z": 1, "a": False})
            self.assertEqual(path.read_bytes(), b'{"a":false,"z":1}\n')
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            with self.assertRaisesRegex(tail.CaptureRefusalError, "tail-output-exists"):
                create_private_json(path, {"a": True})

    def test_private_inputs_reject_symlink_and_world_readable(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            secret = root / "secret"
            secret.write_text("synthetic", encoding="ascii")
            secret.chmod(0o644)
            with self.assertRaisesRegex(tail.CaptureRefusalError, "private-file-permissions"):
                private_file(secret)
            secret.chmod(0o600)
            link = root / "link"
            link.symlink_to(secret)
            with self.assertRaisesRegex(tail.CaptureRefusalError, "private-file-link"):
                private_file(link)

    def test_config_shape_fails_before_container_access(self) -> None:
        with patch.object(tail, "labelled_container") as access:
            with self.assertRaisesRegex(tail.CaptureRefusalError, "tail-input-shape"):
                tail.capture({"format": "claimcore-fenced-tail-capture-input-1"}, "pinned")
            access.assert_not_called()


if __name__ == "__main__":
    unittest.main()
