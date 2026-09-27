"""Synthetic, isolated post-handoff WAL capture; not a recovery certificate."""

import re
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

from fenced_tail_io import (
    CONTAINER,
    LSN,
    SEGMENT,
    SYSTEM_ID,
    CaptureRefusal,
    command,
    create_private_json,
    hash_file,
    private_directory,
    private_file,
    regular_segment,
    require,
    require_container_segment,
)

__all__ = ["CaptureRefusal"]


def waldump_executable():
    if sys.platform == "darwin":
        candidates = ("/opt/homebrew/opt/libpq/bin/pg_waldump",)
    elif sys.platform.startswith("linux"):
        candidates = ("/usr/lib/postgresql/18/bin/pg_waldump", "/usr/bin/pg_waldump")
    else:
        raise CaptureRefusal("wal-inspection-tool-unavailable")
    for candidate in candidates:
        if Path(candidate).is_file():
            version = command(
                [candidate, "--version"],
                maximum=64,
                stage="wal-inspection-tool",
            )
            require(version == "pg_waldump (PostgreSQL) 18.6", "wal-inspection-version")
            return candidate
    raise CaptureRefusal("wal-inspection-tool-unavailable")


def lsn_number(value):
    require(isinstance(value, str) and LSN.fullmatch(value), "wal-lsn")
    high, low = value.split("/")
    return (int(high, 16) << 32) + int(low, 16)


def segment_number(value, segment_bytes, timeline):
    require(isinstance(value, str) and SEGMENT.fullmatch(value), "wal-segment")
    require(int(value[:8], 16) == timeline, "wal-timeline")
    require(segment_bytes > 0 and (1 << 32) % segment_bytes == 0, "wal-segment-size")
    per_log = (1 << 32) // segment_bytes
    index = int(value[16:], 16)
    require(index < per_log, "wal-segment")
    return int(value[8:16], 16) * per_log + index


def segments_between(first, last, segment_bytes, timeline, limit):
    start = segment_number(first, segment_bytes, timeline)
    end = segment_number(last, segment_bytes, timeline)
    require(start <= end and end - start < limit, "wal-range")
    per_log = (1 << 32) // segment_bytes
    prefix = f"{timeline:08X}"
    return [
        f"{prefix}{number // per_log:08X}{number % per_log:08X}"
        for number in range(start, end + 1)
    ]


def first_segment_for_horizon(horizon, segment_bytes, timeline):
    require(segment_bytes > 0 and (1 << 32) % segment_bytes == 0, "wal-segment-size")
    number = lsn_number(horizon) // segment_bytes
    per_log = (1 << 32) // segment_bytes
    return f"{timeline:08X}{number // per_log:08X}{number % per_log:08X}"


def cluster_query(container, role, sql, *, stage):
    return command(
        [
            "docker",
            "exec",
            "-u",
            "postgres",
            container,
            "psql",
            "-XAtq",
            "-U",
            role,
            "-d",
            "postgres",
            "-c",
            sql,
        ],
        stage=stage,
    )


def cluster_identity(container, role):
    raw = cluster_query(
        container,
        role,
        "SELECT s.system_identifier::text || '|' || c.timeline_id::text || '|' || "
        "pg_size_bytes(current_setting('wal_segment_size'))::text "
        "FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c",
        stage="cluster-identity",
    )
    pieces = raw.split("|")
    require(len(pieces) == 3 and SYSTEM_ID.fullmatch(pieces[0]), "wal-cluster-identity")
    require(all(piece.isdecimal() for piece in pieces[1:]), "wal-cluster-identity")
    return pieces[0], int(pieces[1]), int(pieces[2])


def labelled_container(container, pinned_image):
    require(
        isinstance(container, str) and CONTAINER.fullmatch(container), "container-id"
    )
    raw = command(
        [
            "docker",
            "inspect",
            "--format",
            '{{.Config.Image}}|{{index .Config.Labels "org.claimcore.restore-test"}}',
            container,
        ],
        stage="container-inspect",
    )
    pieces = raw.split("|")
    require(
        len(pieces) == 2
        and pieces[0] == pinned_image
        and re.fullmatch(r"[0-9]{1,12}", pieces[1]),
        "isolated-container-label",
    )


def switch_range(container, role, horizon, segment_bytes, timeline):
    before = cluster_query(
        container,
        role,
        "SELECT pg_current_wal_flush_lsn()::text",
        stage="wal-horizon-query",
    )
    require(lsn_number(before) >= lsn_number(horizon), "wal-horizon-ahead")
    first = first_segment_for_horizon(horizon, segment_bytes, timeline)
    switched = cluster_query(
        container,
        role,
        "WITH switched AS MATERIALIZED (SELECT pg_switch_wal() AS lsn) "
        "SELECT lsn::text || '|' || pg_walfile_name(lsn) FROM switched",
        stage="wal-switch-query",
    )
    pieces = switched.split("|")
    require(len(pieces) == 2, "wal-switch")
    require(lsn_number(pieces[0]) > lsn_number(horizon), "wal-switch-no-progress")
    return first, pieces[1], pieces[0]


def capture_cluster(
    name,
    container,
    role,
    horizon,
    expected_system,
    expected_timeline,
    scratch,
    archive,
    recipient,
    limit,
):
    system, timeline, segment_bytes = cluster_identity(container, role)
    require(
        system == expected_system and timeline == expected_timeline,
        "wal-cluster-mismatch",
    )
    require(
        segment_bytes >= 1024 * 1024 and segment_bytes <= 1024 * 1024 * 1024,
        "wal-segment-size",
    )
    first, last, final_lsn = switch_range(
        container, role, horizon, segment_bytes, timeline
    )
    waldump = waldump_executable()
    names = segments_between(first, last, segment_bytes, timeline, limit)
    plain_root = scratch / "fenced-tail" / name
    archive_root = archive / "fenced-tail" / name
    plain_root.mkdir(mode=0o700, parents=True, exist_ok=False)
    archive_root.mkdir(mode=0o700, parents=True, exist_ok=False)
    objects = []
    for segment in names:
        plain = plain_root / segment
        source = f"/var/lib/postgresql/18/docker/pg_wal/{segment}"
        require_container_segment(container, source)
        command(
            [
                "docker",
                "cp",
                f"{container}:{source}",
                str(plain),
            ],
            maximum=0,
            stage="wal-segment-copy",
        )
        regular_segment(plain, segment_bytes)
        command(
            [waldump, "--quiet", "--limit=1", "--path", str(plain_root), segment],
            maximum=4096,
            stage="wal-inspection",
        )
        relative = f"fenced-tail/{name}/{segment}.age"
        encrypted = archive / relative
        require(not encrypted.exists(), "wal-object-exists")
        command(
            [
                "age",
                "--encrypt",
                "--recipient",
                recipient,
                "--output",
                str(encrypted),
                str(plain),
            ],
            maximum=0,
            stage="wal-encryption",
        )
        private_file(encrypted)
        sha, length = hash_file(encrypted)
        objects.append(
            {
                "objectId": str(uuid.uuid4()),
                "cluster": name.upper(),
                "relativePath": relative,
                "sha256": sha,
                "bytes": length,
                "segment": segment,
                "segmentBytes": segment_bytes,
            }
        )
    return {
        "containerId": container,
        "systemId": system,
        "timeline": timeline,
        "registeredWalHorizon": horizon,
        "finalLsn": final_lsn,
        "walSegmentBytes": segment_bytes,
        "objects": objects,
    }


def capture(config, pinned_image):
    require(
        isinstance(config, dict)
        and set(config)
        == {
            "format",
            "primaryContainerId",
            "witnessContainerId",
            "primaryOwnerRole",
            "witnessOwnerRole",
            "scratchRoot",
            "archiveRoot",
            "ageIdentityFile",
            "outputFile",
            "primaryRegisteredWalHorizon",
            "witnessRegisteredWalHorizon",
            "primarySystemId",
            "witnessSystemId",
            "primaryTimeline",
            "witnessTimeline",
            "maximumWalSegments",
        },
        "tail-input-shape",
    )
    require(
        config["format"] == "claimcore-fenced-tail-capture-input-1", "tail-input-format"
    )
    scratch = private_directory(config["scratchRoot"])
    archive = private_directory(config["archiveRoot"])
    identity = private_file(config["ageIdentityFile"])
    output = Path(config["outputFile"])
    require(output.parent == scratch and not output.exists(), "tail-output-scope")
    require(
        type(config["maximumWalSegments"]) is int
        and 1 <= config["maximumWalSegments"] <= 1000,
        "wal-range",
    )
    primary, witness = config["primaryContainerId"], config["witnessContainerId"]
    require(primary != witness, "container-identity")
    for name in ("primary", "witness"):
        role = config[name + "OwnerRole"]
        require(
            isinstance(role, str)
            and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", role) is not None,
            "owner-role-invalid",
        )
    for container in (primary, witness):
        labelled_container(container, pinned_image)
    recipient = command(
        ["age-keygen", "-y", str(identity)], maximum=128, stage="recipient-derivation"
    )
    require(recipient.startswith("age1") and len(recipient) == 62, "age-recipient")
    data = {
        "format": "claimcore-fenced-tail-capture-1",
        "scope": "synthetic-only",
        "realDataReady": False,
        "capturedAt": datetime.now(timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z"),
    }
    for name in ("primary", "witness"):
        data[name] = capture_cluster(
            name,
            config[name + "ContainerId"],
            config[name + "OwnerRole"],
            config[name + "RegisteredWalHorizon"],
            config[name + "SystemId"],
            config[name + "Timeline"],
            scratch,
            archive,
            recipient,
            config["maximumWalSegments"],
        )
    create_private_json(output, data)
    return data
