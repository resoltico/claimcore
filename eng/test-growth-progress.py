#!/usr/bin/env python3
"""Comparison progress is observational, source-qualified and never a partial pass."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

specification = importlib.util.spec_from_file_location(
    "growth_progress_stage", Path(__file__).with_name("growth-compare.py")
)
stage = importlib.util.module_from_spec(specification)
specification.loader.exec_module(stage)


class ProgressTests(unittest.TestCase):
    def report(self, directory):
        return {"directory": str(directory), "reference": "reference-source",
                "candidate": "candidate-source", "candidatePatchSha256": "source-patch",
                "harnessSha256": {"reference": "ref-harness", "candidate": "candidate-harness"}}

    def snapshot(self, directory):
        return json.loads((directory / "comparison-progress.json").read_text())

    def test_atomic_observation_never_inherits_a_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            report = self.report(directory)
            report.update(passed=True, completeQualification=True, runs=[{}, {}])
            stage.progress(report, "run-started", side="candidate", pair=2)
            actual = self.snapshot(directory)
            self.assertEqual(actual["status"], "incomplete")
            self.assertNotIn("passed", actual)
            self.assertEqual(actual["completedRuns"], 2)
            self.assertEqual(actual["harnessSha256"], report["harnessSha256"])
            self.assertEqual(actual["candidatePatchSha256"], "source-patch")
            self.assertFalse((directory / "comparison-progress.json.writing").exists())

    def test_alternating_sides_publish_owned_paths_before_child_work(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            report = self.report(directory)
            workspace = directory / "owned"
            observed = []

            def run(checkout, destination, _environment, _preparation, _profile):
                current = self.snapshot(directory)
                self.assertEqual(current["phase"], "run-started")
                self.assertEqual(current["completedRuns"], len(observed))
                self.assertEqual(current["active"]["checkout"], str(checkout))
                self.assertEqual(current["active"]["reportDirectory"], str(destination))
                self.assertEqual(current["active"]["readProgressRoot"],
                                 str(checkout / "artifacts/verification/growth"))
                self.assertNotIn("passed", current)
                observed.append(checkout.name)
                return {"workloads": {}}

            preparations = {"reference": {}, "candidate": {}}
            with patch.object(stage, "run_workloads", side_effect=run), \
                    patch.object(stage, "compare", return_value={"passed": False}) as compare:
                result = stage.alternate(workspace, report, preparations, None)
            self.assertFalse(result["passed"])
            self.assertEqual(observed, ["reference", "candidate"] * 3)
            self.assertEqual(self.snapshot(directory)["completedRuns"], 6)
            self.assertEqual(self.snapshot(directory)["phase"], "run-collected")
            compare.assert_called_once()

    def test_interrupted_candidate_keeps_incomplete_state_and_first_side(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            report = self.report(directory)

            def run(checkout, *_arguments):
                if checkout.name == "candidate":
                    raise InterruptedError("Synthetic interrupted child")
                return {"workloads": {}}

            with patch.object(stage, "run_workloads", side_effect=run), \
                    patch.object(stage, "compare") as compare:
                with self.assertRaises(InterruptedError):
                    stage.alternate(directory / "owned", report,
                                    {"reference": {}, "candidate": {}}, None)
            current = self.snapshot(directory)
            self.assertEqual(current["status"], "incomplete")
            self.assertEqual(current["active"]["side"], "candidate")
            self.assertEqual(current["completedRuns"], 1)
            self.assertNotIn("passed", current)
            compare.assert_not_called()

    def test_failed_preparation_finishes_with_authoritative_failed_result(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch.dict(os.environ, {"CLAIMCORE_TEST_REPORT_DIRECTORY": temporary}, clear=True), \
                    patch.object(stage, "candidate_snapshot", return_value=("candidate-source", b"", None)), \
                    patch.object(stage, "git", return_value="reference-source"), \
                    patch.object(stage, "review_equivalence", return_value=None), \
                    patch.object(stage.subprocess, "run"), \
                    patch.object(stage, "prepare_checkouts", side_effect=RuntimeError("Synthetic prepare failure")), \
                    patch.object(stage, "clean_containers"):
                self.assertEqual(stage.main("reference"), 1)
            directory = next(Path(temporary).glob("growth-compare-*"))
            current = self.snapshot(directory)
            final = json.loads(Path(current["resultFile"]).read_text())
            self.assertEqual(current["status"], "finished")
            self.assertEqual(current["phase"], "finished")
            self.assertEqual(current["completedRuns"], 0)
            self.assertFalse(final["passed"])
            self.assertIn("Synthetic prepare failure", final["error"])
            self.assertNotIn("passed", current)


if __name__ == "__main__":
    unittest.main()
