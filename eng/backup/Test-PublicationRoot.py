#!/usr/bin/env python3
"""Source-pinned public root loader stays closed without reviewed bytes."""

import hashlib
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True
from deployment_common import DeploymentRefusal
from deployment_publication_root import reviewed_publication_root


class PublicationRootTests(unittest.TestCase):
    def test_absent_then_exact_pinned_public_bytes(self):
        self.assertIsNone(reviewed_publication_root())
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            private, public = root / "root.key", root / "root.pub"
            assert (
                subprocess.run(
                    [
                        "openssl",
                        "genpkey",
                        "-algorithm",
                        "ED25519",
                        "-out",
                        str(private),
                    ],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                ).returncode
                == 0
            )
            assert (
                subprocess.run(
                    [
                        "openssl",
                        "pkey",
                        "-in",
                        str(private),
                        "-pubout",
                        "-out",
                        str(public),
                    ],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                ).returncode
                == 0
            )
            key = public.read_bytes()
            sha = hashlib.sha256(key).hexdigest()
            self.assertEqual(
                reviewed_publication_root(pinned_sha=sha, source=public), key
            )
            with self.assertRaisesRegex(DeploymentRefusal, "publication-root-digest"):
                reviewed_publication_root(pinned_sha="0" * 64, source=public)
            link = root / "linked.pub"
            link.symlink_to(public)
            with self.assertRaisesRegex(DeploymentRefusal, "publication-root-file"):
                reviewed_publication_root(pinned_sha=sha, source=link)
            public.write_bytes(b"tampered public key")
            with self.assertRaisesRegex(DeploymentRefusal, "publication-root-digest"):
                reviewed_publication_root(pinned_sha=sha, source=public)


if __name__ == "__main__":
    unittest.main()
