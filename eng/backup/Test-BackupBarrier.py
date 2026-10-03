#!/usr/bin/env python3
"""Synthetic private-pipe backup lease framing and uncertainty tests."""

import hashlib
import os
import sys
import tempfile
import unittest
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path
from unittest.mock import patch

sys.dont_write_bytecode = True
from backup_barrier import BarrierUnknownError, capture_with_barrier, decode, frame
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, utc

FRAME_FORMAT = "claimcore-backup-barrier-frame-1"
PATH_NAMES = (
    "primaryCiphertextPath",
    "witnessCiphertextPath",
    "checkpointPath",
    "cycleManifestPath",
    "cycleManifestSignaturePath",
)


class Controller:
    def __init__(
        self,
        expected: JsonObject,
        now: datetime,
        *,
        cycle_root: Path,
        held_change: JsonObject | None = None,
        finish_change: JsonObject | None = None,
        observed_change: JsonObject | None = None,
        eof_at: str | None = None,
    ) -> None:
        self.expected = expected
        self.now = now
        self.held_change = held_change or {}
        self.finish_change = finish_change or {}
        self.observed_change = observed_change or {}
        self.eof_at = eof_at
        self.cycle_root = cycle_root
        self.expected_primary_digest: str | None = None
        self.events: list[str] = []
        self.lease: JsonObject = {}
        self.receipt: JsonObject = {}

    def _begin(self, request: JsonObject) -> bytes:
        self.lease = {
            "format": FRAME_FORMAT,
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

    def _finish(self, request: JsonObject) -> bytes:
        if self.expected_primary_digest is not None:
            actual = hashlib.sha256(Path(request["primaryCiphertextPath"]).read_bytes()).hexdigest()
            if actual != self.expected_primary_digest:
                msg = "owner-object-changed"
                raise DeploymentRefusalError(msg)
        self.receipt = {
            "format": FRAME_FORMAT,
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

    def exchange(self, raw: bytes) -> bytes:
        request = decode(raw)
        self.events.append(request["kind"])
        if self.eof_at == request["kind"]:
            return b""
        if request["kind"] == "BEGIN":
            return self._begin(request)
        if request["kind"] == "FINISH":
            return self._finish(request)
        if request["kind"] == "ABORT":
            return frame({"format": FRAME_FORMAT, "kind": "ABORTED"})
        msg = "Unexpected private-pipe frame"
        raise AssertionError(msg)

    def observe(self, raw: bytes) -> bytes:
        request = decode(raw)
        self.events.append(request["kind"])
        return frame({**self.receipt, "kind": "OBSERVED", **self.observed_change})


def paths(root: Path, checkpoint: Path) -> dict[str, str]:
    answer = {}
    for name in PATH_NAMES:
        parent = checkpoint if name == "checkpointPath" else root
        path = parent / (name + ".synthetic")
        path.write_bytes(b"synthetic bytes")
        path.chmod(0o600)
        answer[name] = str(path)
    return answer


class BackupBarrierTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name).resolve()
        self.root.chmod(0o700)
        self.cycle, self.checkpoint = self.root / "cycle", self.root / "checkpoint"
        self.cycle.mkdir(mode=0o700)
        self.checkpoint.mkdir(mode=0o700)
        self.expected: JsonObject = {
            "installationId": str(uuid.uuid4()),
            "lineageId": str(uuid.uuid4()),
            "epoch": 1,
            "writerGeneration": 1,
        }
        self.now = datetime.now(UTC).replace(microsecond=0)

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _controller(
        self,
        *,
        held_change: JsonObject | None = None,
        finish_change: JsonObject | None = None,
        observed_change: JsonObject | None = None,
        eof_at: str | None = None,
    ) -> Controller:
        return Controller(
            self.expected,
            self.now,
            cycle_root=self.cycle,
            held_change=held_change,
            finish_change=finish_change,
            observed_change=observed_change,
            eof_at=eof_at,
        )

    def _capture(
        self, controller: Controller, action: Callable[[JsonObject], JsonObject] | None = None
    ) -> JsonObject:
        return capture_with_barrier(
            controller,
            action or (lambda _lease: paths(self.cycle, self.checkpoint)),
            self.expected,
            checkpoint_root=self.checkpoint,
            now=self.now,
        )

    def test_exact_lease_and_separate_readback(self) -> None:
        controller = self._controller()
        result = self._capture(controller)
        self.assertEqual(result["status"], "captured-unverified-observed")
        self.assertFalse(result["retained"])
        self.assertFalse(result["realDataReady"])
        self.assertEqual(result["leaseId"], controller.lease["leaseId"])
        self.assertEqual(controller.events, ["BEGIN", "FINISH", "OBSERVE"])

    def test_held_lease_refusals(self) -> None:
        for changes, category in (
            ({"nonce": "0" * 64}, "barrier-held-identity"),
            ({"validUntil": utc(self.now - timedelta(seconds=1))}, "barrier-held-expired"),
            ({"cutoffHash": "invalid"}, "barrier-held-evidence"),
        ):
            with self.assertRaisesRegex(DeploymentRefusalError, category):
                self._capture(self._controller(held_change=changes))
        with self.assertRaisesRegex(DeploymentRefusalError, "barrier-frame-limit"):
            self._capture(self._controller(eof_at="BEGIN"))

    def test_refusal_before_finish_aborts(self) -> None:
        before_finish = self._controller()
        with self.assertRaisesRegex(DeploymentRefusalError, "barrier-capture-files"):
            self._capture(before_finish, lambda _lease: {})
        self.assertEqual(before_finish.events, ["BEGIN", "ABORT"])

    def test_interrupt_before_finish_aborts(self) -> None:
        before_finish = self._controller()

        def interrupted(_lease: JsonObject) -> JsonObject:
            raise KeyboardInterrupt

        with self.assertRaises(KeyboardInterrupt):
            self._capture(before_finish, interrupted)
        self.assertEqual(before_finish.events, ["BEGIN", "ABORT"])

    def test_uncertainty_after_finish_is_retained(self) -> None:
        cases = (
            (["BEGIN", "FINISH"], self._controller(finish_change={"receiptSha256": "invalid"})),
            (
                ["BEGIN", "FINISH", "OBSERVE"],
                self._controller(observed_change={"witnessHash": "e" * 64}),
            ),
            (["BEGIN", "FINISH"], self._controller(eof_at="FINISH")),
        )
        for events, controller in cases:
            with self.assertRaisesRegex(BarrierUnknownError, "barrier-finish-uncertain") as failure:
                self._capture(controller)
            self.assertEqual(failure.exception.lease_id, controller.lease["leaseId"])
            self.assertEqual(controller.events, events)

    def test_interrupts_preserve_phase_and_reconciliation_identity(self) -> None:
        for stage, method in (("FINISH", "exchange"), ("OBSERVE", "observe")):
            controller = self._controller()
            original: Callable[[bytes], bytes] = getattr(controller, method)

            def interrupted(
                raw: bytes,
                stage: str = stage,
                controller: Controller = controller,
                original: Callable[[bytes], bytes] = original,
            ) -> bytes:
                if decode(raw)["kind"] == stage:
                    controller.events.append(stage)
                    raise KeyboardInterrupt
                result: bytes = original(raw)
                return result

            with (
                patch.object(controller, method, side_effect=interrupted),
                self.assertRaises(BarrierUnknownError) as failure,
            ):
                self._capture(controller)
            self.assertEqual(failure.exception.lease_id, controller.lease["leaseId"])
            self.assertNotIn("ABORT", controller.events)
            self.assertTrue(any(self.cycle.iterdir()))

    def test_bytes_changed_during_finish_are_uncertain(self) -> None:
        changed = self._controller()

        def changed_bytes(_lease: JsonObject) -> JsonObject:
            saved = paths(self.cycle, self.checkpoint)
            path = Path(saved["primaryCiphertextPath"])
            changed.expected_primary_digest = hashlib.sha256(path.read_bytes()).hexdigest()
            path.write_bytes(b"changed synthetic bytes")
            return saved

        with self.assertRaisesRegex(BarrierUnknownError, "barrier-finish-uncertain"):
            self._capture(changed, changed_bytes)
        self.assertEqual(changed.events, ["BEGIN", "FINISH"])

    def test_linked_paths_are_refused_before_finish(self) -> None:
        linked = self._controller()

        def linked_path(_lease: JsonObject) -> JsonObject:
            saved = paths(self.cycle, self.checkpoint)
            link = self.root / "linked-primary"
            link.symlink_to(Path(saved["primaryCiphertextPath"]))
            saved["primaryCiphertextPath"] = str(link)
            return saved

        with self.assertRaisesRegex(DeploymentRefusalError, "linked-private-path"):
            self._capture(linked, linked_path)
        self.assertEqual(linked.events, ["BEGIN", "ABORT"])
        hardlinked = self._controller()

        def hardlinked_path(_lease: JsonObject) -> JsonObject:
            saved = paths(self.cycle, self.checkpoint)
            linked_file = self.cycle / "hardlinked-primary"
            os.link(saved["primaryCiphertextPath"], linked_file)
            saved["primaryCiphertextPath"] = str(linked_file)
            return saved

        with self.assertRaisesRegex(DeploymentRefusalError, "barrier-capture-hardlink"):
            self._capture(hardlinked, hardlinked_path)
        self.assertEqual(hardlinked.events, ["BEGIN", "ABORT"])


if __name__ == "__main__":
    unittest.main()
