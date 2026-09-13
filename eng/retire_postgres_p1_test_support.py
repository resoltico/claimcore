"""Shared synthetic fixtures for the one-off GHCR p1 retirement tests."""

import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

source = Path(__file__).with_name("retire_postgres_p1_versions.py")
spec = importlib.util.spec_from_file_location("retire_postgres_p1_versions", source)
retire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retire)

CONTEXT = {
    "GITHUB_ACTIONS": "true",
    "GITHUB_EVENT_NAME": "workflow_dispatch",
    "GITHUB_REPOSITORY": "resoltico/claimcore",
    "GITHUB_REF": "refs/heads/main",
    "CLAIMCORE_RETIRE_EXECUTE": "false",
}


def version(row):
    version_id, digest, tag = row
    return {
        "id": version_id,
        "name": digest,
        "url": f"{retire.API}{retire.VERSIONS}/{version_id}",
        "metadata": {"package_type": "container", "container": {"tags": [tag]}},
    }


def valid_fetch(path, accept="application/vnd.github+json"):
    if path == retire.MAIN_BASELINE:
        return {"containerImage": retire.BASELINE_IMAGE}
    version_id = int(path.rsplit("/", 1)[1])
    for row in (retire.BASELINE_VERSION, *retire.TARGETS):
        if row[0] == version_id:
            return version(row)
    raise AssertionError("Unexpected package API target")


class RetirementTestCase(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        baseline = Path(self.temporary.name) / "baseline.json"
        baseline.write_text(
            json.dumps({"containerImage": retire.BASELINE_IMAGE}), encoding="utf-8"
        )
        self.baseline_patch = patch.object(retire, "BASELINE_FILE", baseline)
        self.baseline_patch.start()
        self.addCleanup(self.baseline_patch.stop)
        self.deleted = []

    def run_retirement(self, context=None, fetch=valid_fetch):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            retire.run(
                fetch, self.deleted.append, CONTEXT if context is None else context
            )
        return output.getvalue()
