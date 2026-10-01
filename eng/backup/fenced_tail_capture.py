"""Synthetic, isolated post-handoff WAL capture; not a recovery certificate."""

import re
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from backup_types import Json, JsonObject
from fenced_tail_io import (
    CONTAINER,
    SYSTEM_ID,
    CaptureRefusalError,
    command,
    create_private_json,
    hash_file,
    private_directory,
    private_file,
    regular_segment,
    require,
    require_container_segment,
)
from fenced_tail_wal import (
    first_segment_for_horizon,
    lsn_number,
    segments_between,
    waldump_executable,
)

__all__ = ["CaptureRefusalError", "capture", "private_file", "require"]

MIN_SEGMENT_BYTES = 1024 * 1024
MAX_SEGMENT_BYTES = 1024 * 1024 * 1024
MAX_WAL_SEGMENTS = 1000
RECIPIENT_LENGTH = 62
IDENTITY_PIECES = 3
PAIR = 2
INPUT_FIELDS = {
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
}


@dataclass(frozen=True)
class ClusterTarget:
    """One disposable cluster to capture the fenced tail from."""

    name: str
    container: str
    role: str
    horizon: str
    system_id: str
    timeline: int


@dataclass(frozen=True)
class CaptureSite:
    """Where captured segments are staged and archived, and the recipient they are sealed to."""

    scratch: Path
    archive: Path
    recipient: str
    limit: int


def cluster_query(container: str, role: str, sql: str, *, stage: str) -> str:
    """Run one SQL statement as the owner role inside a disposable cluster."""
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


def cluster_identity(container: str, role: str) -> tuple[str, int, int]:
    """Read the cluster's system identifier, timeline and WAL segment size."""
    raw = cluster_query(
        container,
        role,
        "SELECT s.system_identifier::text || '|' || c.timeline_id::text || '|' || "
        "pg_size_bytes(current_setting('wal_segment_size'))::text "
        "FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c",
        stage="cluster-identity",
    )
    pieces = raw.split("|")
    require(
        len(pieces) == IDENTITY_PIECES and SYSTEM_ID.fullmatch(pieces[0]), "wal-cluster-identity"
    )
    require(all(piece.isdecimal() for piece in pieces[1:]), "wal-cluster-identity")
    return pieces[0], int(pieces[1]), int(pieces[2])


def labelled_container(container: Json, pinned_image: str) -> None:
    """Require a container running the pinned image with the restore-test label."""
    require(isinstance(container, str) and CONTAINER.fullmatch(container), "container-id")
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
        len(pieces) == PAIR
        and pieces[0] == pinned_image
        and re.fullmatch(r"[0-9]{1,12}", pieces[1]),
        "isolated-container-label",
    )


def switch_range(
    container: str, role: str, horizon: str, segment_bytes: int, timeline: int
) -> tuple[str, str, str]:
    """Switch WAL and return the first segment, last segment and final LSN of the tail."""
    before = cluster_query(
        container, role, "SELECT pg_current_wal_flush_lsn()::text", stage="wal-horizon-query"
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
    require(len(pieces) == PAIR, "wal-switch")
    require(lsn_number(pieces[0]) > lsn_number(horizon), "wal-switch-no-progress")
    return first, pieces[1], pieces[0]


def _capture_segment(
    target: ClusterTarget, site: CaptureSite, segment: str, segment_bytes: int, waldump: str
) -> JsonObject:
    plain_root = site.scratch / "fenced-tail" / target.name
    plain = plain_root / segment
    source = f"/var/lib/postgresql/18/docker/pg_wal/{segment}"
    require_container_segment(target.container, source)
    command(
        ["docker", "cp", f"{target.container}:{source}", str(plain)],
        maximum=0,
        stage="wal-segment-copy",
    )
    regular_segment(plain, segment_bytes)
    command(
        [waldump, "--quiet", "--limit=1", "--path", str(plain_root), segment],
        maximum=4096,
        stage="wal-inspection",
    )
    relative = f"fenced-tail/{target.name}/{segment}.age"
    encrypted = site.archive / relative
    require(not encrypted.exists(), "wal-object-exists")
    command(
        ["age", "--encrypt", "--recipient", site.recipient, "--output", str(encrypted), str(plain)],
        maximum=0,
        stage="wal-encryption",
    )
    private_file(encrypted)
    sha, length = hash_file(encrypted)
    return {
        "objectId": str(uuid.uuid4()),
        "cluster": target.name.upper(),
        "relativePath": relative,
        "sha256": sha,
        "bytes": length,
        "segment": segment,
        "segmentBytes": segment_bytes,
    }


def capture_cluster(target: ClusterTarget, site: CaptureSite) -> JsonObject:
    """Capture and seal every WAL segment the cluster wrote after its registered horizon."""
    system, timeline, segment_bytes = cluster_identity(target.container, target.role)
    require(system == target.system_id and timeline == target.timeline, "wal-cluster-mismatch")
    require(MIN_SEGMENT_BYTES <= segment_bytes <= MAX_SEGMENT_BYTES, "wal-segment-size")
    first, last, final_lsn = switch_range(
        target.container, target.role, target.horizon, segment_bytes, timeline
    )
    waldump = waldump_executable()
    names = segments_between(first, last, segment_bytes, timeline, site.limit)
    (site.scratch / "fenced-tail" / target.name).mkdir(mode=0o700, parents=True, exist_ok=False)
    (site.archive / "fenced-tail" / target.name).mkdir(mode=0o700, parents=True, exist_ok=False)
    objects = [_capture_segment(target, site, segment, segment_bytes, waldump) for segment in names]
    return {
        "containerId": target.container,
        "systemId": system,
        "timeline": timeline,
        "registeredWalHorizon": target.horizon,
        "finalLsn": final_lsn,
        "walSegmentBytes": segment_bytes,
        "objects": objects,
    }


def _validate_input(config: JsonObject) -> tuple[Path, Path, Path, Path]:
    require(isinstance(config, dict) and set(config) == INPUT_FIELDS, "tail-input-shape")
    require(config["format"] == "claimcore-fenced-tail-capture-input-1", "tail-input-format")
    scratch = private_directory(config["scratchRoot"])
    archive = private_directory(config["archiveRoot"])
    identity = private_file(config["ageIdentityFile"])
    output = Path(config["outputFile"])
    require(output.parent == scratch and not output.exists(), "tail-output-scope")
    require(
        type(config["maximumWalSegments"]) is int
        and 1 <= config["maximumWalSegments"] <= MAX_WAL_SEGMENTS,
        "wal-range",
    )
    require(config["primaryContainerId"] != config["witnessContainerId"], "container-identity")
    for name in ("primary", "witness"):
        role = config[name + "OwnerRole"]
        require(
            isinstance(role, str)
            and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", role) is not None,
            "owner-role-invalid",
        )
    return scratch, archive, identity, output


def capture(config: JsonObject, pinned_image: str) -> JsonObject:
    """Capture the post-handoff WAL of both disposable clusters and record it privately."""
    scratch, archive, identity, output = _validate_input(config)
    for container in (config["primaryContainerId"], config["witnessContainerId"]):
        labelled_container(container, pinned_image)
    recipient = command(
        ["age-keygen", "-y", str(identity)], maximum=128, stage="recipient-derivation"
    )
    require(recipient.startswith("age1") and len(recipient) == RECIPIENT_LENGTH, "age-recipient")
    site = CaptureSite(scratch, archive, recipient, config["maximumWalSegments"])
    data: JsonObject = {
        "format": "claimcore-fenced-tail-capture-1",
        "scope": "synthetic-only",
        "realDataReady": False,
        "capturedAt": datetime.now(UTC).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }
    for name in ("primary", "witness"):
        target = ClusterTarget(
            name,
            config[name + "ContainerId"],
            config[name + "OwnerRole"],
            config[name + "RegisteredWalHorizon"],
            config[name + "SystemId"],
            config[name + "Timeline"],
        )
        data[name] = capture_cluster(target, site)
    create_private_json(output, data)
    return data
