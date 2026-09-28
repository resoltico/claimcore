"""Bounded signed final-WAL object shape and aggregate digest."""

import hashlib
import re
import uuid

from deployment_common import require

OBJECT_FIELDS = {
    "objectId",
    "cluster",
    "relativePath",
    "ciphertextSha256",
    "ciphertextBytes",
    "walSegment",
    "walSegmentBytes",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _objects(tail, report):
    items = tail["walObjects"]
    require(isinstance(items, list) and 2 <= len(items) <= 2000, "fenced-tail-objects")
    identities, paths, clusters = set(), set(), set()
    for item in items:
        require(
            isinstance(item, dict) and set(item) == OBJECT_FIELDS,
            "fenced-tail-object-shape",
        )
        cluster = item["cluster"]
        require(cluster in ("PRIMARY", "WITNESS"), "fenced-tail-object-cluster")
        path = item["relativePath"]
        require(
            isinstance(path, str)
            and 1 <= len(path) <= 512
            and all(
                part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
                for part in path.split("/")
            ),
            "fenced-tail-object-path",
        )
        segment = item["walSegment"]
        require(
            isinstance(segment, str) and re.fullmatch(r"[0-9A-F]{24}", segment),
            "fenced-tail-object-segment",
        )
        require(
            int(segment[:8], 16) == report[cluster.lower() + "Timeline"],
            "fenced-tail-timeline",
        )
        size, length = item["walSegmentBytes"], item["ciphertextBytes"]
        require(
            type(size) is int
            and 1024 * 1024 <= size <= 1024 * 1024 * 1024
            and size & (size - 1) == 0
            and type(length) is int
            and size < length <= 1024 * 1024 * 1024 * 1024
            and _uuid(item["objectId"])
            and _digest(item["ciphertextSha256"]),
            "fenced-tail-object-size",
        )
        identities.add(item["objectId"])
        paths.add(path)
        clusters.add(cluster)
    require(
        len(identities) == len(items) and len(paths) == len(items),
        "fenced-tail-duplicate",
    )
    require(clusters == {"PRIMARY", "WITNESS"}, "fenced-tail-cluster-coverage")


def final_objects_digest(tail):
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
