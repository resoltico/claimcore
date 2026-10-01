"""Read every signed final-WAL ciphertext from independent archive custody."""

import hashlib
import os
import stat
from pathlib import Path

from backup_types import JsonObject
from deployment_common import (
    DeploymentRefusalError,
    is_sha256,
    is_uuid,
    private_path,
    refuse,
    require,
)
from deployment_fenced_objects import (
    CLUSTERS,
    MAX_OBJECTS,
    MIN_OBJECTS,
    OBJECT_FIELDS,
    final_objects_digest,
    valid_object_sizes,
    valid_relative_path,
    valid_segment_name,
)


def _check_item(item: JsonObject) -> None:
    require(isinstance(item, dict) and set(item) == OBJECT_FIELDS, "final-wal-shape")
    require(is_uuid(item["objectId"]), "final-wal-id")
    require(item["cluster"] in CLUSTERS, "final-wal-cluster")
    require(valid_relative_path(item["relativePath"]), "final-wal-path")
    require(
        is_sha256(item["ciphertextSha256"])
        and valid_object_sizes(item)
        and valid_segment_name(item["walSegment"]),
        "final-wal-size",
    )


def _verify_ciphertext(archive: Path, item: JsonObject) -> None:
    try:
        path = private_path(archive / item["relativePath"])
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except OSError:
        msg = "final-wal-missing"
        raise DeploymentRefusalError(msg) from None
    with os.fdopen(descriptor, "rb") as source:
        status = os.fstat(source.fileno())
        require(
            stat.S_ISREG(status.st_mode)
            and status.st_nlink == 1
            and status.st_size == item["ciphertextBytes"],
            "final-wal-file-invalid",
        )
        actual = hashlib.file_digest(source, "sha256").hexdigest()
    require(actual == item["ciphertextSha256"], "final-wal-changed")


def read_final_wal(config: JsonObject) -> JsonObject:
    """Verify each signed final-WAL object against archive custody and summarize the set."""
    items = config.get("finalWalObjects")
    if not (isinstance(items, list) and MIN_OBJECTS <= len(items) <= MAX_OBJECTS):
        refuse("final-wal-set")
    archive = private_path(config["finalWalArchiveRoot"], directory=True, owner=False)
    for item in items:
        _check_item(item)
        _verify_ciphertext(archive, item)
    require(
        len({item["objectId"] for item in items}) == len(items)
        and len({item["relativePath"] for item in items}) == len(items)
        and {item["cluster"] for item in items} == set(CLUSTERS),
        "final-wal-incomplete",
    )
    return {
        "finalWalObjects": items,
        "finalWalObjectCount": len(items),
        "finalWalObjectSha256": final_objects_digest({"walObjects": items}),
    }
