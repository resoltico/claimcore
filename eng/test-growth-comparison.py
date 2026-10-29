#!/usr/bin/env python3
"""Independent numeric and workload-identity controls for growth comparison."""

import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch

from growth_comparison import compare, differences, metric_result, validate_fingerprint
from growth_reference import prepare, replace_once

specification = importlib.util.spec_from_file_location("growth_stage", Path(__file__).with_name("growth-compare.py"))
stage = importlib.util.module_from_spec(specification)
specification.loader.exec_module(stage)


def fingerprint(definition):
    canonical = json.dumps(definition, sort_keys=True, separators=(",", ":"))
    return {"definition": definition, "canonicalJson": canonical,
            "sha256": hashlib.sha256(canonical.encode()).hexdigest()}


def runs():
    workloads = {}
    for key in ("write/10000/1", "write/100000/1", "write/100000/16",
                "cardinality/1000", "read/search/100000", "read/filtered/100000"):
        workloads[key] = {
            "writesPerSecond": 100, "requestsPerSecond": 100, "p95Milliseconds": 10, "p99Milliseconds": 20,
            "verificationMilliseconds": 100, "workloadFingerprint": fingerprint({"key": key}),
        }
    return [{"side": side, "hasReadWorkloads": True, "workloads": copy.deepcopy(workloads)}
            for side in ["reference", "candidate"] * 3]


class GrowthComparisonTests(unittest.TestCase):
    def test_profile_selection_refuses_undeclared_or_conflicting_modes(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertIsNone(stage.requested_profile())
        for value in ("10000:1", "100000:1", "100000:16"):
            with patch.dict(os.environ, {"CLAIMCORE_GROWTH_PROFILE":value}, clear=True):
                self.assertEqual(stage.requested_profile(), value)
        with patch.dict(os.environ, {"CLAIMCORE_GROWTH_PROFILE":"1000:1"}, clear=True):
            with self.assertRaises(ValueError):
                stage.requested_profile()
        with patch.dict(os.environ, {"CLAIMCORE_GROWTH_COMPARISON":"1"}, clear=True):
            with self.assertRaises(ValueError):
                stage.requested_profile()

    def test_full_completion_does_not_accept_diagnostic_counts(self):
        full = {"result":"completed", "completed":3, "expected":3,
                "requiredReadProfiles":2, "completedReadProfiles":2}
        diagnostic = {"result":"completed", "completed":1, "expected":1,
                      "requiredReadProfiles":6, "completedReadProfiles":0}
        preparation = {"hasReadWorkloads":True}
        self.assertEqual(stage.validate_completion(full, preparation, None), 2)
        self.assertEqual(stage.validate_completion(diagnostic, preparation, "10000:1"), 0)
        with self.assertRaises(ValueError):
            stage.validate_completion(diagnostic, preparation, None)
        with self.assertRaises(ValueError):
            stage.validate_completion({**full, "completedReadProfiles":0}, preparation, None)

    def test_diagnostic_scope_never_returns_qualification_success(self):
        self.assertEqual(stage.qualification_exit(True, True), 0)
        self.assertEqual(stage.qualification_exit(False, True), 1)
        self.assertEqual(stage.qualification_exit(False, False), 1)
        self.assertEqual(stage.qualification_exit(True, False), 2)

    def test_explicit_diagnostic_scope_keeps_three_pairs_and_default_gate_closed(self):
        samples = runs()
        for run in samples:
            run["hasReadWorkloads"] = False
            run["workloads"] = {key:value for key,value in run["workloads"].items()
                                if key in {"write/10000/1", "cardinality/1000"}}
        with self.assertRaisesRegex(ValueError, "Missing required"):
            compare(samples, {})
        self.assertTrue(compare(samples, {}, {"write/10000/1"})["passed"])
        for run in samples[1::2]:
            run["workloads"]["write/10000/1"]["writesPerSecond"] = 30
        self.assertFalse(compare(samples, {}, {"write/10000/1"})["passed"])

    def test_interrupted_evidence_keeps_reports_and_excludes_transient_key_rings(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            growth = root / "growth"
            run = growth / "run-id"
            keys = growth / "keys-owned-test"
            run.mkdir(parents=True)
            keys.mkdir()
            (run / "run.json").write_text('{"result":"incomplete"}\n')
            (keys / "key.xml").write_text("synthetic private-material fixture\n")
            destination = root / "retained"
            stage.retain_failed_reports(growth, destination)
            self.assertEqual((destination / "run-id/run.json").read_text(), '{"result":"incomplete"}\n')
            self.assertFalse((destination / "keys-owned-test").exists())

    def test_candidate_snapshot_preserves_uncommitted_control_and_exact_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(["git", "init", "--quiet", str(root)], check=True)
            source = root / "source.fs"
            source.write_text("baseline\n")
            subprocess.run(["git", "-C", str(root), "add", "source.fs"], check=True)
            subprocess.run(["git", "-C", str(root), "-c", "user.name=Growth test",
                            "-c", "user.email=growth@example.test", "-c", "core.hooksPath=/dev/null",
                            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fixture baseline"], check=True)
            revision, patch, digest = stage.candidate_snapshot(root)
            self.assertEqual(patch, b"")
            self.assertIsNone(digest)
            source.write_text("uncommitted intervention\n")
            after, patch, digest = stage.candidate_snapshot(root)
            self.assertEqual(after, revision)
            self.assertEqual(digest, hashlib.sha256(patch).hexdigest())
            self.assertIn(b"+uncommitted intervention", patch)
            self.assertEqual(stage.git(root, "rev-list", "--count", "HEAD"), "1")
            (root / "untracked.fs").write_text("not captured\n")
            with self.assertRaisesRegex(ValueError, "untracked"):
                stage.candidate_snapshot(root)

    def test_medians_resist_one_outlier_and_retain_all_samples(self):
        row = metric_result("writesPerSecond", [100, 110, 1000], [90, 100, 900])
        self.assertEqual(row["referenceMedian"], 110)
        self.assertEqual(row["candidateMedian"], 100)
        self.assertEqual(row["referenceRuns"], [100, 110, 1000])
        self.assertTrue(row["passed"])

    def test_tolerances_have_inclusive_boundaries(self):
        for name in ("writesPerSecond", "requestsPerSecond"):
            self.assertTrue(metric_result(name, [100] * 3, [85] * 3)["passed"])
            self.assertFalse(metric_result(name, [100] * 3, [84.99] * 3)["passed"])
        for name in ("p95Milliseconds", "p99Milliseconds", "verificationMilliseconds"):
            self.assertTrue(metric_result(name, [100] * 3, [130] * 3)["passed"])
            self.assertFalse(metric_result(name, [100] * 3, [130.01] * 3)["passed"])

    def test_missing_invalid_or_nonpositive_samples_refuse(self):
        for values in ([1, 1], [0, 1, 1], [-1, 1, 1], [float("nan"), 1, 1], [float("inf")] * 3):
            with self.assertRaises(ValueError):
                metric_result("p95Milliseconds", values, [1] * 3)

    def test_fingerprint_mismatch_names_changed_member(self):
        before = {"workloadFingerprint": fingerprint({"query": {"order": "RECENT"}})}
        after = {"workloadFingerprint": fingerprint({"query": {"order": "REFERENCE"}})}
        with self.assertRaisesRegex(ValueError, r"differing members: \$\.query.order"):
            validate_fingerprint(before, after, None)
        self.assertEqual(differences([1], [2]), ["$[0]"])
        self.assertEqual(differences({"cursor": None}, {}), ["$.cursor"])
        self.assertEqual(differences(True, 1), ["$"])

    def test_corrupt_digest_and_definition_refuse(self):
        candidate = {"workloadFingerprint": fingerprint({"writers": 1})}
        wrong = copy.deepcopy(candidate)
        wrong["workloadFingerprint"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "digest"):
            validate_fingerprint(candidate, wrong, None)
        wrong = copy.deepcopy(candidate)
        wrong["workloadFingerprint"]["definition"]["writers"] = 16
        with self.assertRaisesRegex(ValueError, "canonical JSON"):
            validate_fingerprint(candidate, wrong, None)

    def test_historical_identity_requires_recorded_equivalence(self):
        candidate = {"workloadFingerprint": fingerprint({"writers": 1})}
        with self.assertRaisesRegex(ValueError, "manual equivalence"):
            validate_fingerprint({}, candidate, None)
        self.assertEqual(validate_fingerprint({}, candidate, candidate["workloadFingerprint"]["sha256"]),
                         "manual equivalence review")

    def test_complete_alternating_run_passes_and_detects_real_regression(self):
        samples = runs()
        result = compare(samples, {})
        self.assertTrue(result["passed"])
        self.assertEqual(len(result["metrics"]), 19)
        for run in samples[1::2]:
            run["workloads"]["write/100000/16"]["writesPerSecond"] = 40
        result = compare(samples, {})
        self.assertFalse(result["passed"])
        self.assertEqual(sum(not row["passed"] for row in result["metrics"]), 1)

    def test_incomplete_non_alternating_and_changed_workload_sets_refuse(self):
        for samples in (runs()[:4], list(reversed(runs()))):
            with self.assertRaises(ValueError):
                compare(samples, {})
        samples = runs()
        del samples[4]["workloads"]["read/search/100000"]
        with self.assertRaisesRegex(ValueError, "Missing required"):
            compare(samples, {})
        samples = runs()
        for run in samples[::2]:
            run["hasReadWorkloads"] = False
            del run["workloads"]["read/search/100000"]
            del run["workloads"]["read/filtered/100000"]
        del samples[1]["workloads"]["read/search/100000"]
        with self.assertRaisesRegex(ValueError, "Missing required"):
            compare(samples, {})

    def test_new_read_is_not_applicable_on_reference_that_predates_it(self):
        samples = runs()
        for run in samples[1::2]:
            run["workloads"]["read/summary/100000"] = copy.deepcopy(run["workloads"]["read/search/100000"])
        self.assertEqual(compare(samples, {})["notApplicable"], ["read/summary/100000"])

    def test_harness_adapter_is_narrow_and_rejects_unrecognized_sources(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = root / "tests/ClaimCore.TestSupport/PostgresFixture.fs"
            fixture.parent.mkdir(parents=True)
            fixture.write_text('.WithTmpfsMount("/var/lib/postgresql")\n')
            plan = root / "tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs"
            plan.parent.mkdir(parents=True)
            plan.write_text("modern\n")
            prepared = prepare(root, "owned-control")
            self.assertFalse(prepared["historical"])
            self.assertIn("owned-control", fixture.read_text())
            self.assertEqual(plan.read_text(), "modern\n")
        with self.assertRaisesRegex(ValueError, "Unsupported historical"):
            replace_once("other", "expected", "replacement")

    def test_historical_adapter_preserves_full_qualification_branches(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sources = {
                "tests/ClaimCore.TestSupport/PostgresFixture.fs": '.WithTmpfsMount("/var/lib/postgresql")\n',
                "tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs": (
                    "let private cardinalityWrites cases = cases\n"
                    "for cases in [ 1; 100; 1000 ] do\n| None -> required\n"
                    "if selected.Length = 9 then\n                    6\nrequiredReadProfiles = 6\n"
                ),
                "tests/ClaimCore.IntegrationTests/GrowthReadWorkloads.fs": "for cases in [ 1000; 10000; 100000 ] do\n",
            }
            for relative, source in sources.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(source)
            adapted = prepare(root, "historical-control")
            self.assertTrue(adapted["historical"])
            qualification = (root / "tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs").read_text()
            self.assertIn("else required", qualification)
            self.assertIn("else [ 1; 100; 1000 ]", qualification)
            self.assertIn("selected.Length = 9 || comparison", qualification)
            self.assertIn("if comparison then 2 else 6", qualification)
            reads = (root / "tests/ClaimCore.IntegrationTests/GrowthReadWorkloads.fs").read_text()
            self.assertIn("else [ 1000; 10000; 100000 ]", reads)


if __name__ == "__main__":
    unittest.main()
