"""Bounded signed final-WAL object shape and aggregate digest."""

import hashlib
import re

from backup_types import Json, JsonObject
from deployment_common import is_sha256, is_uuid, require

OBJECT_FIELDS = {
    "objectId",
    "cluster",
    "relativePath",
    "ciphertextSha256",
    "ciphertextBytes",
    "walSegment",
    "walSegmentBytes",
}
CLUSTERS = ("PRIMARY", "WITNESS")
MIN_OBJECTS = 2
MAX_OBJECTS = 2000
MAX_PATH_CHARS = 512
MIN_SEGMENT_BYTES = 1 << 20
MAX_SEGMENT_BYTES = 1 << 30
MAX_CIPHERTEXT_BYTES = 1 << 40


def valid_relative_path(path: Json) -> bool:
    """Whether `path` is a bounded relative path of plain components."""
    return (
        isinstance(path, str)
        and 1 <= len(path) <= MAX_PATH_CHARS
        and all(
            part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
            for part in path.split("/")
        )
    )


def valid_segment_name(segment: Json) -> bool:
    """Whether `segment` names a WAL segment: timeline, log and segment in hexadecimal."""
    return isinstance(segment, str) and re.fullmatch(r"[0-9A-F]{24}", segment) is not None


def valid_object_sizes(item: JsonObject) -> bool:
    """Whether the segment size is a bounded power of two smaller than its ciphertext."""
    size, length = item["walSegmentBytes"], item["ciphertextBytes"]
    return (
        type(size) is int
        and MIN_SEGMENT_BYTES <= size <= MAX_SEGMENT_BYTES
        and size & (size - 1) == 0
        and type(length) is int
        and size < length <= MAX_CIPHERTEXT_BYTES
    )


def _check_object(item: JsonObject, report: JsonObject) -> None:
    require(isinstance(item, dict) and set(item) == OBJECT_FIELDS, "fenced-tail-object-shape")
    cluster = item["cluster"]
    require(cluster in CLUSTERS, "fenced-tail-object-cluster")
    require(valid_relative_path(item["relativePath"]), "fenced-tail-object-path")
    segment = item["walSegment"]
    require(valid_segment_name(segment), "fenced-tail-object-segment")
    require(
        int(segment[:8], 16) == report[cluster.lower() + "Timeline"],
        "fenced-tail-timeline",
    )
    require(
        valid_object_sizes(item)
        and is_uuid(item["objectId"])
        and is_sha256(item["ciphertextSha256"]),
        "fenced-tail-object-size",
    )


def check_objects(tail: JsonObject, report: JsonObject) -> None:
    """Refuse a tail whose WAL objects are malformed, duplicated or miss a cluster."""
    items = tail["walObjects"]
    require(
        isinstance(items, list) and MIN_OBJECTS <= len(items) <= MAX_OBJECTS, "fenced-tail-objects"
    )
    for item in items:
        _check_object(item, report)
    require(
        len({item["objectId"] for item in items}) == len(items)
        and len({item["relativePath"] for item in items}) == len(items),
        "fenced-tail-duplicate",
    )
    require({item["cluster"] for item in items} == set(CLUSTERS), "fenced-tail-cluster-coverage")


def final_objects_digest(tail: JsonObject) -> str:
    """Digest the final WAL objects in canonical path order."""
    lines = [
        "|".join(
            (
                item["objectId"],
                item["cluster"],
                item["relativePath"],
                item["ciphertextSha256"],
                str(item["ciphertextBytes"]),
                item["walSegment"],
                str(item["walSegmentBytes"]),
            )
        )
        for item in sorted(tail["walObjects"], key=lambda value: value["relativePath"])
    ]
    return hashlib.sha256(("\n".join(lines) + "\n").encode("ascii")).hexdigest()
