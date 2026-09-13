"""Execution guards, p2 preservation, and resumability controls."""

import json

from retire_postgres_p1_test_support import (
    CONTEXT,
    RetirementTestCase,
    retire,
    valid_fetch,
)


class RetirementGuardTests(RetirementTestCase):
    def test_dry_run_checks_every_version_without_deletion(self):
        visited = []

        def fetch(path, accept="application/vnd.github+json"):
            visited.append(path)
            return valid_fetch(path, accept)

        self.run_retirement(fetch=fetch)
        self.assertEqual(self.deleted, [])
        self.assertEqual(len(visited), 11)  # main baseline, p2, nine p1 versions
        self.assertEqual(len(set(visited)), 11)

    def test_confirmed_execution_only_uses_nine_exact_version_paths(self):
        context = CONTEXT | {
            "CLAIMCORE_RETIRE_EXECUTE": "true",
            "CLAIMCORE_RETIRE_CONFIRM": "DELETE 9 P1 VERSIONS",
        }
        self.run_retirement(context)
        self.assertEqual(
            self.deleted, [f"{retire.VERSIONS}/{row[0]}" for row in retire.TARGETS]
        )
        self.assertNotIn(
            f"{retire.VERSIONS}/{retire.BASELINE_VERSION[0]}", self.deleted
        )

    def test_wrong_workflow_context_or_mode_blocks_before_network(self):
        for key, value in (
            ("GITHUB_ACTIONS", "false"),
            ("GITHUB_EVENT_NAME", "push"),
            ("GITHUB_REPOSITORY", "other/claimcore"),
            ("GITHUB_REF", "refs/heads/test"),
            ("CLAIMCORE_RETIRE_EXECUTE", "yes"),
        ):
            with self.subTest(key=key):

                def forbidden_fetch(*_args):
                    self.fail("The guard reached the GitHub API")

                with self.assertRaises(retire.RetirementError):
                    self.run_retirement(CONTEXT | {key: value}, forbidden_fetch)
                self.assertEqual(self.deleted, [])

    def test_missing_exact_confirmation_blocks_before_network(self):
        context = CONTEXT | {
            "CLAIMCORE_RETIRE_EXECUTE": "true",
            "CLAIMCORE_RETIRE_CONFIRM": "delete",
        }
        with self.assertRaises(retire.RetirementError):
            self.run_retirement(
                context, lambda *_args: self.fail("The guard reached the API")
            )
        self.assertEqual(self.deleted, [])

    def test_local_or_remote_baseline_drift_blocks_all_deletions(self):
        retire.BASELINE_FILE.write_text(
            '{"containerImage":"postgres:latest"}', encoding="utf-8"
        )
        with self.assertRaises(retire.RetirementError):
            self.run_retirement()
        retire.BASELINE_FILE.write_text(
            json.dumps({"containerImage": retire.BASELINE_IMAGE}), encoding="utf-8"
        )

        def drifted_fetch(path, accept="application/vnd.github+json"):
            return (
                {"containerImage": "postgres:latest"}
                if path == retire.MAIN_BASELINE
                else valid_fetch(path, accept)
            )

        with self.assertRaises(retire.RetirementError):
            self.run_retirement(fetch=drifted_fetch)
        self.assertEqual(self.deleted, [])

    def test_partial_retirement_is_idempotent_and_counts_absent_versions(self):
        absent_ids = {retire.TARGETS[0][0], retire.TARGETS[4][0]}

        def partially_absent(path, accept="application/vnd.github+json"):
            if path in {f"{retire.VERSIONS}/{version_id}" for version_id in absent_ids}:
                raise retire.VersionAbsent("Already removed")
            return valid_fetch(path, accept)

        output = self.run_retirement(fetch=partially_absent)
        self.assertIn("present=7, already absent=2", output)
        self.assertEqual(self.deleted, [])
        context = CONTEXT | {
            "CLAIMCORE_RETIRE_EXECUTE": "true",
            "CLAIMCORE_RETIRE_CONFIRM": "DELETE 9 P1 VERSIONS",
        }
        self.run_retirement(context, partially_absent)
        self.assertEqual(
            self.deleted,
            [
                f"{retire.VERSIONS}/{row[0]}"
                for row in retire.TARGETS
                if row[0] not in absent_ids
            ],
        )
