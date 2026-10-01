"""Synthetic independent-archive readback of every signed final-WAL ciphertext."""

import hashlib
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

sys.dont_write_bytecode = True
from deployment_common import DeploymentRefusalError
from deployment_final_wal import read_final_wal


class FinalWalArchiveTests(unittest.TestCase):
    def test_complete_actual_set_and_missing_or_changed_copy(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            root.chmod(0o700)
            items = []
            paths = []
            for index, cluster in enumerate(("PRIMARY", "WITNESS"), 1):
                directory = root / "fenced-tail" / cluster.lower()
                directory.mkdir(parents=True, mode=0o700)
                directory.parent.chmod(0o700)
                path = directory / "000000010000000000000001.age"
                data = bytes([index]) * ((1 << 20) + 32)
                path.write_bytes(data)
                path.chmod(0o600)
                paths.append(path)
                items.append(
                    {
                        "objectId": str(uuid.uuid4()),
                        "cluster": cluster,
                        "relativePath": str(path.relative_to(root)),
                        "ciphertextSha256": hashlib.sha256(data).hexdigest(),
                        "ciphertextBytes": len(data),
                        "walSegment": "000000010000000000000001",
                        "walSegmentBytes": 1 << 20,
                    }
                )
            config = {"finalWalArchiveRoot": str(root), "finalWalObjects": items}
            verified = read_final_wal(config)
            self.assertEqual(verified["finalWalObjectCount"], 2)
            self.assertEqual(len(verified["finalWalObjectSha256"]), 64)
            linked = root / "linked.age"
            linked.hardlink_to(paths[0])
            with self.assertRaisesRegex(DeploymentRefusalError, "final-wal-file-invalid"):
                read_final_wal(config)
            linked.unlink()
            paths[1].unlink()
            with self.assertRaises(DeploymentRefusalError):
                read_final_wal(config)
            paths[1].write_bytes(bytes([3]) * ((1 << 20) + 32))
            paths[1].chmod(0o600)
            with self.assertRaisesRegex(DeploymentRefusalError, "final-wal-changed"):
                read_final_wal(config)


if __name__ == "__main__":
    unittest.main()
