"""Exact package metadata, allowlist, and REST failure controls."""

import os
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError

from retire_postgres_p1_test_support import RetirementTestCase, retire, valid_fetch


class RetirementMetadataTests(RetirementTestCase):
    def test_active_version_or_p1_metadata_drift_blocks_all_deletions(self):
        for changed_id in (retire.BASELINE_VERSION[0], retire.TARGETS[-1][0]):
            for field in ("id", "name", "url", "tag", "package_type", "metadata"):
                with self.subTest(changed_id=changed_id, field=field):

                    def drifted_fetch(
                        path,
                        accept="application/vnd.github+json",
                        changed_id=changed_id,
                        field=field,
                    ):
                        actual = valid_fetch(path, accept)
                        if path == f"{retire.VERSIONS}/{changed_id}":
                            if field == "tag":
                                actual["metadata"]["container"]["tags"] = ["latest"]
                            elif field == "package_type":
                                actual["metadata"]["package_type"] = "npm"
                            else:
                                actual[field] = "changed"
                        return actual

                    with self.assertRaises(retire.RetirementError):
                        self.run_retirement(fetch=drifted_fetch)
                    self.assertEqual(self.deleted, [])

    def test_only_exact_obsolete_get_404_means_already_absent(self):
        error = HTTPError("https://api.github.com/", 404, "not found", None, None)
        with (
            patch.dict(os.environ, {"GH_TOKEN": "synthetic-token"}),
            patch.object(retire, "urlopen", side_effect=error),
        ):
            with self.assertRaises(retire.VersionAbsent):
                retire.request(f"{retire.VERSIONS}/{retire.TARGETS[0][0]}")
            with self.assertRaises(retire.RetirementError) as active:
                retire.request(f"{retire.VERSIONS}/{retire.BASELINE_VERSION[0]}")
            self.assertNotIsInstance(active.exception, retire.VersionAbsent)
            with self.assertRaises(retire.RetirementError) as deletion:
                retire.request(
                    f"{retire.VERSIONS}/{retire.TARGETS[0][0]}", method="DELETE"
                )
            self.assertNotIsInstance(deletion.exception, retire.VersionAbsent)

    def test_allowlist_byte_change_or_active_digest_cannot_enter_plan(self):
        changed_allowlist = Path(self.temporary.name) / "changed.tsv"
        changed_allowlist.write_bytes(retire.ALLOWLIST_FILE.read_bytes() + b"\n")
        with (
            patch.object(retire, "ALLOWLIST_FILE", changed_allowlist),
            self.assertRaises(retire.RetirementError),
        ):
            retire.load_targets()
        changed_row = (
            retire.TARGETS[0][0],
            retire.BASELINE_VERSION[1],
            retire.TARGETS[0][2],
        )
        with (
            patch.object(retire, "TARGETS", (changed_row, *retire.TARGETS[1:])),
            self.assertRaises(retire.RetirementError),
        ):
            self.run_retirement()
        self.assertEqual(self.deleted, [])
