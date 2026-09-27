"""Read every signed final-WAL ciphertext from independent archive custody."""

import hashlib
import os
import re
import stat
import uuid

from deployment_common import DeploymentRefusal, private_path, require
from deployment_fenced_objects import OBJECT_FIELDS, final_objects_digest


def read_final_wal(config):
    items = config.get("finalWalObjects")
    require(isinstance(items, list) and 2 <= len(items) <= 2000, "final-wal-set")
    archive = private_path(config["finalWalArchiveRoot"], directory=True, owner=False)
    identities, paths, clusters = set(), set(), set()
    for item in items:
        require(
            isinstance(item, dict) and set(item) == OBJECT_FIELDS, "final-wal-shape"
        )
        identity = uuid.UUID(item["objectId"])
        require(identity.int != 0 and str(identity) == item["objectId"], "final-wal-id")
        cluster = item["cluster"]
        require(cluster in ("PRIMARY", "WITNESS"), "final-wal-cluster")
        relative = item["relativePath"]
        require(
            isinstance(relative, str)
            and 1 <= len(relative) <= 512
            and all(
                part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
                for part in relative.split("/")
            ),
            "final-wal-path",
        )
        require(
            isinstance(item["ciphertextSha256"], str)
            and re.fullmatch(r"[0-9a-f]{64}", item["ciphertextSha256"])
            and type(item["ciphertextBytes"]) is int
            and type(item["walSegmentBytes"]) is int
            and 1 << 20 <= item["walSegmentBytes"] <= 1 << 30
            and item["walSegmentBytes"] & (item["walSegmentBytes"] - 1) == 0
            and item["walSegmentBytes"] < item["ciphertextBytes"] <= 1 << 40
            and isinstance(item["walSegment"], str)
            and re.fullmatch(r"[0-9A-F]{24}", item["walSegment"]),
            "final-wal-size",
        )
        try:
            path = private_path(archive / relative)
            descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
        except OSError:
            raise DeploymentRefusal("final-wal-missing") from None
        with os.fdopen(descriptor, "rb") as source:
            status = os.fstat(source.fileno())
            require(
                stat.S_ISREG(status.st_mode)
                and status.st_nlink == 1
                and status.st_size == item["ciphertextBytes"],
                "final-wal-file-invalid",
            )
            actual = hashlib.file_digest(source, "sha256").hexdigest()
        require(
            actual == item["ciphertextSha256"],
            "final-wal-changed",
        )
        identities.add(item["objectId"])
        paths.add(relative)
        clusters.add(cluster)
    require(
        len(identities) == len(items)
        and len(paths) == len(items)
        and clusters == {"PRIMARY", "WITNESS"},
        "final-wal-incomplete",
    )
    return {
        "finalWalObjects": items,
        "finalWalObjectCount": len(items),
        "finalWalObjectSha256": final_objects_digest({"walObjects": items}),
    }
