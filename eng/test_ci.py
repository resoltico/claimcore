import contextlib
from copy import deepcopy
import io
import json
from pathlib import Path
import tempfile
import unittest

from ci_policy import gate_failures, qualification
from validate_advisories import expected_frameworks, findings


class QualificationTests(unittest.TestCase):
    def test_changes_require_only_applicable_pr_checks(self):
        for path in (
            "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config",
            "global.json", "tests/ClaimCore.Tests/packages.lock.json", "web/package-lock.json",
            "eng/ci_policy.py", ".github/workflows/ci.yml",
        ):
            with self.subTest(path=path):
                self.assertEqual(
                    qualification("pull_request", [path]),
                    {"docs_only": False, "advisories": True, "compose_restart": True},
                )

        self.assertEqual(
            qualification("pull_request", ["contracts/client.ts", "web/src/main.tsx"]),
            {"docs_only": False, "advisories": False, "compose_restart": True},
        )
        self.assertEqual(
            qualification("pull_request", ["docs/contributing.md"]),
            {"docs_only": True, "advisories": False, "compose_restart": False},
        )
        self.assertEqual(
            qualification("push", ["docs/contributing.md"]),
            {"docs_only": False, "advisories": True, "compose_restart": True},
        )

    def test_gate_rejects_missing_and_incomplete_jobs(self):
        success = {
            name: {"result": "success"}
            for name in (
                "configuration", "lint", "unit", "release_artifacts", "integration", "server",
                "frontend", "acceptance_native", "acceptance", "advisories", "compose_restart",
            )
        }
        full = {"docs_only": False, "advisories": True, "compose_restart": True}
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(gate_failures(success, full), [])
            self.assertTrue(gate_failures({}, full))
            missing_compose = dict(success)
            del missing_compose["compose_restart"]
            self.assertEqual(gate_failures(missing_compose, full), ["Missing Gate dependency: compose_restart"])
            skipped_advisories = success | {"advisories": {"result": "skipped"}}
            self.assertEqual(
                gate_failures(skipped_advisories, full),
                ["advisories must be success, got skipped."],
            )
            unrelated_pr = {"docs_only": False, "advisories": False, "compose_restart": False}
            self.assertEqual(
                gate_failures(skipped_advisories | {"compose_restart": {"result": "skipped"}}, unrelated_pr),
                [],
            )
            self.assertEqual(
                gate_failures(skipped_advisories, unrelated_pr),
                ["compose_restart must be skipped, got success."],
            )


class AdvisoryTests(unittest.TestCase):
    def test_solution_owns_expected_projects_and_lockfiles_own_frameworks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "src" / "Test.fsproj"
            project.parent.mkdir()
            solution = root / "Example.slnx"
            solution.write_text('<Solution><Project Path="src/Test.fsproj" /></Solution>', encoding="utf-8")
            targets = {"net11.0": {}, "net11.0/linux-x64": {}, "net10.0": {}}
            project.with_name("packages.lock.json").write_text(
                json.dumps({"dependencies": targets}), encoding="utf-8"
            )
            self.assertEqual(expected_frameworks(solution), {project.resolve(): {"net10.0", "net11.0"}})
            targets["net11.0/linux-x64"]["Example.Dependency"] = {"type": "Transitive", "resolved": "1.0.0"}
            project.with_name("packages.lock.json").write_text(
                json.dumps({"dependencies": targets}), encoding="utf-8"
            )
            with self.assertRaisesRegex(ValueError, "cannot cover RID-specific package"):
                expected_frameworks(solution)

    def test_advisory_report_rejects_missing_and_malformed_coverage(self):
        project = Path("/workspace/Example.fsproj")
        expected = {project: {"net11.0"}}
        report = {
            "version": 1,
            "parameters": "--vulnerable --include-transitive",
            "sources": ["https://example.invalid"],
            "projects": [
                {"path": str(project), "frameworks": [{"framework": "net11.0", "topLevelPackages": []}]},
            ],
        }
        self.assertEqual(findings(report, expected), [])

        for invalid in (
            {},
            report | {"version": 999},
            report | {"parameters": "--vulnerable"},
            report | {"sources": []},
            report | {"sources": "not a list"},
            report | {"problems": [{"text": "The advisory source was unavailable."}]},
            report | {"projects": []},
            report | {"projects": [{"path": str(project), "frameworks": []}]},
        ):
            with self.subTest(report=invalid), self.assertRaises(ValueError):
                findings(invalid, expected)

        with self.assertRaisesRegex(ValueError, "incomplete coverage"):
            findings(report, expected | {Path("/workspace/Omitted.fsproj"): {"net11.0"}})

        vulnerable = deepcopy(report)
        vulnerable["projects"][0]["frameworks"][0]["transitivePackages"] = [{
            "id": "Example.Dependency",
            "vulnerabilities": [{"severity": "Low", "advisoryurl": "https://example.invalid/advisory"}],
        }]
        self.assertEqual(
            findings(vulnerable, expected),
            ["/workspace/Example.fsproj [net11.0] Example.Dependency Low https://example.invalid/advisory"],
        )


if __name__ == "__main__":
    unittest.main()
