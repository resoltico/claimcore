#!/usr/bin/env python3
"""Synthetic private-pipe backup lease framing and uncertainty tests."""

import hashlib
import os
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from backup_barrier import BarrierUnknown, capture_with_barrier, decode, frame
from deployment_common import DeploymentRefusal, utc


class Controller:
    def __init__(
        self,
        expected,
        now,
        *,
        held_change=None,
        finish_change=None,
        observed_change=None,
        eof_at=None,
        cycle_root=None,
    ):
        self.expected = expected
        self.now = now
        self.held_change = held_change or {}
        self.finish_change = finish_change or {}
        self.observed_change = observed_change or {}
        self.eof_at = eof_at
        self.cycle_root = cycle_root
        self.expected_primary_digest = None
        self.events = []
        self.lease = None
        self.receipt = None

    def exchange(self, raw):
        request = decode(raw)
        self.events.append(request["kind"])
        if self.eof_at == request["kind"]:
            return b""
        if request["kind"] == "BEGIN":
            self.lease = {
                "format": "claimcore-backup-barrier-frame-1",
                "kind": "HELD",
                "nonce": request["nonce"],
                "leaseId": str(uuid.uuid4()),
                **self.expected,
                "cutoffSequence": 10,
                "cutoffHash": "a" * 64,
                "primarySystemId": "1111111111111111111",
                "primaryTimeline": 1,
                "witnessSystemId": "2222222222222222222",
                "witnessTimeline": 1,
                "maintenanceEvidenceSha256": "b" * 64,
                "checkedAt": utc(self.now),
                "validUntil": utc(self.now + timedelta(minutes=10)),
                "cycleRoot": str(self.cycle_root),
                **self.held_change,
            }
            return frame(self.lease)
        if request["kind"] == "FINISH":
            if self.expected_primary_digest is not None:
                actual = hashlib.sha256(
                    Path(request["primaryCiphertextPath"]).read_bytes()
                ).hexdigest()
                if actual != self.expected_primary_digest:
                    raise DeploymentRefusal("owner-object-changed")
            self.receipt = {
                "format": "claimcore-backup-barrier-frame-1",
                "kind": "SEALED",
                "nonce": request["nonce"],
                "leaseId": self.lease["leaseId"],
                "cycleReceiptId": str(uuid.uuid4()),
                "witnessSequence": 11,
                "witnessHash": "c" * 64,
                "receiptSha256": "d" * 64,
                **self.finish_change,
            }
            return frame(self.receipt)
        if request["kind"] == "ABORT":
            return frame(
                {"format": "claimcore-backup-barrier-frame-1", "kind": "ABORTED"}
            )
        raise AssertionError("Unexpected private-pipe frame")

    def observe(self, raw):
        request = decode(raw)
        self.events.append(request["kind"])
        return frame({**self.receipt, "kind": "OBSERVED", **self.observed_change})


def paths(root, checkpoint):
    names = (
        "primaryCiphertextPath",
        "witnessCiphertextPath",
        "checkpointPath",
        "cycleManifestPath",
        "cycleManifestSignaturePath",
    )
    answer = {}
    for name in names:
        parent = checkpoint if name == "checkpointPath" else root
        path = parent / (name + ".synthetic")
        path.write_bytes(b"synthetic bytes")
        path.chmod(0o600)
        answer[name] = str(path)
    return answer


class BackupBarrierTests(unittest.TestCase):
    def test_exact_lease_and_separate_readback(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            cycle, checkpoint = root / "cycle", root / "checkpoint"
            cycle.mkdir(mode=0o700)
            checkpoint.mkdir(mode=0o700)
            expected = {
                "installationId": str(uuid.uuid4()),
                "lineageId": str(uuid.uuid4()),
                "epoch": 1,
                "writerGeneration": 1,
            }
            now = datetime.now(timezone.utc).replace(microsecond=0)
            controller = Controller(expected, now, cycle_root=cycle)
            result = capture_with_barrier(
                controller,
                lambda lease: paths(cycle, checkpoint),
                expected,
                checkpoint_root=checkpoint,
                now=now,
            )
            self.assertEqual(result["status"], "captured-unverified-observed")
            self.assertFalse(result["retained"])
            self.assertFalse(result["realDataReady"])
            self.assertEqual(controller.events, ["BEGIN", "FINISH", "OBSERVE"])
            for changes, category in (
                ({"nonce": "0" * 64}, "barrier-held-identity"),
                (
                    {"validUntil": utc(now - timedelta(seconds=1))},
                    "barrier-held-expired",
                ),
                ({"cutoffHash": "invalid"}, "barrier-held-evidence"),
            ):
                with self.assertRaisesRegex(DeploymentRefusal, category):
                    capture_with_barrier(
                        Controller(
                            expected, now, cycle_root=cycle, held_change=changes
                        ),
                        lambda lease: paths(cycle, checkpoint),
                        expected,
                        checkpoint_root=checkpoint,
                        now=now,
                    )
            before_finish = Controller(expected, now, cycle_root=cycle)
            with self.assertRaisesRegex(DeploymentRefusal, "barrier-capture-files"):
                capture_with_barrier(
                    before_finish,
                    lambda lease: {},
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(before_finish.events, ["BEGIN", "ABORT"])
            missing_reply = Controller(
                expected,
                now,
                cycle_root=cycle,
                finish_change={"receiptSha256": "invalid"},
            )
            with self.assertRaisesRegex(BarrierUnknown, "barrier-finish-uncertain"):
                capture_with_barrier(
                    missing_reply,
                    lambda lease: paths(cycle, checkpoint),
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(missing_reply.events, ["BEGIN", "FINISH"])
            divergent = Controller(
                expected,
                now,
                cycle_root=cycle,
                observed_change={"witnessHash": "e" * 64},
            )
            with self.assertRaisesRegex(BarrierUnknown, "barrier-finish-uncertain"):
                capture_with_barrier(
                    divergent,
                    lambda lease: paths(cycle, checkpoint),
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(divergent.events, ["BEGIN", "FINISH", "OBSERVE"])
            finished_eof = Controller(expected, now, cycle_root=cycle, eof_at="FINISH")
            with self.assertRaisesRegex(BarrierUnknown, "barrier-finish-uncertain"):
                capture_with_barrier(
                    finished_eof,
                    lambda lease: paths(cycle, checkpoint),
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(finished_eof.events, ["BEGIN", "FINISH"])
            with self.assertRaisesRegex(DeploymentRefusal, "barrier-frame-limit"):
                capture_with_barrier(
                    Controller(expected, now, cycle_root=cycle, eof_at="BEGIN"),
                    lambda lease: paths(cycle, checkpoint),
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            changed = Controller(expected, now, cycle_root=cycle)

            def changed_bytes(_lease):
                saved = paths(cycle, checkpoint)
                path = Path(saved["primaryCiphertextPath"])
                changed.expected_primary_digest = hashlib.sha256(
                    path.read_bytes()
                ).hexdigest()
                path.write_bytes(b"changed synthetic bytes")
                return saved

            with self.assertRaisesRegex(BarrierUnknown, "barrier-finish-uncertain"):
                capture_with_barrier(
                    changed,
                    changed_bytes,
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(changed.events, ["BEGIN", "FINISH"])
            linked = Controller(expected, now, cycle_root=cycle)

            def linked_path(_lease):
                saved = paths(cycle, checkpoint)
                source = Path(saved["primaryCiphertextPath"])
                link = root / "linked-primary"
                link.symlink_to(source)
                saved["primaryCiphertextPath"] = str(link)
                return saved

            with self.assertRaisesRegex(DeploymentRefusal, "linked-private-path"):
                capture_with_barrier(
                    linked, linked_path, expected, checkpoint_root=checkpoint, now=now
                )
            self.assertEqual(linked.events, ["BEGIN", "ABORT"])
            hardlinked = Controller(expected, now, cycle_root=cycle)

            def hardlinked_path(_lease):
                saved = paths(cycle, checkpoint)
                linked_file = cycle / "hardlinked-primary"
                os.link(saved["primaryCiphertextPath"], linked_file)
                saved["primaryCiphertextPath"] = str(linked_file)
                return saved

            with self.assertRaisesRegex(DeploymentRefusal, "barrier-capture-hardlink"):
                capture_with_barrier(
                    hardlinked,
                    hardlinked_path,
                    expected,
                    checkpoint_root=checkpoint,
                    now=now,
                )
            self.assertEqual(hardlinked.events, ["BEGIN", "ABORT"])


if __name__ == "__main__":
    unittest.main()
