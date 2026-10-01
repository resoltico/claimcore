"""Archived WAL segments: encrypted archiving, local attestation and inventory verification."""

import hashlib
import json
import re
import subprocess
import sys
import tempfile
from collections.abc import Iterable
from dataclasses import dataclass
from itertools import islice, pairwise
from pathlib import Path

import inventory
from backup_types import JsonObject
from managed_basebackup import postgres_control
from managed_capture import save_attestation
from managed_common import (
    CLUSTERS,
    decrypt_blocks,
    digest,
    metadata,
    private_path,
    require,
    tool,
    verify_signature,
)
from managed_wal_archive import WAL_NAME

HISTORY_LIMIT = 64 * 1024
HISTORY_LINE = r"[0-9]+\s+[0-9A-F]+/[0-9A-F]+\s+.+"
LOG_SHIFT = 32
SEGMENT_NAME_LENGTH = 24
TIMELINE_DIGITS = 8
LOG_END = 16
IDENTITY_PREFIX = 3
DUPLICATE_PROBE = 2
LIMIT = {"limit": "wal-decryption-limit", "failure": "wal-decryption-failed"}


def plaintext_digest(config: JsonObject, ciphertext: Path) -> str:
    """Return the SHA-256 of a WAL copy's plaintext without writing it anywhere."""
    sha = hashlib.sha256()
    for block in decrypt_blocks(config, ciphertext, **LIMIT):
        sha.update(block)
    return sha.hexdigest()


def _decrypt_to(config: JsonObject, ciphertext: Path, destination: Path) -> int:
    total = 0
    with destination.open("xb") as output:
        for block in decrypt_blocks(config, ciphertext, **LIMIT):
            total += len(block)
            output.write(block)
    return total


def parse_wal(config: JsonObject, ciphertext: Path, segment: str, segment_bytes: int) -> None:
    """Decrypt a WAL copy to scratch and prove it parses as a segment or a timeline history."""
    with tempfile.TemporaryDirectory(prefix="claimcore-wal-check-") as temporary:
        work = Path(temporary)
        work.chmod(0o700)
        source = work / segment
        total = _decrypt_to(config, ciphertext, source)
        if segment.endswith(".history"):
            require(total <= HISTORY_LIMIT, "timeline-history-size")
            lines = source.read_text("utf-8").splitlines()
            require(
                any(re.fullmatch(HISTORY_LINE, line) for line in lines if not line.startswith("#")),
                "timeline-history-format",
            )
            return
        require(total == segment_bytes, "wal-segment-size")
        result = subprocess.run(
            [
                tool("pg_waldump", "pg_waldump (PostgreSQL) 18.6"),
                "--quiet",
                "--limit=1",
                str(source),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(result.returncode == 0, "wal-record-unreadable")


def _sidecar(directory: Path, segment: str, cluster: str) -> tuple[Path, JsonObject]:
    ciphertext = private_path(directory / (segment + ".age"))
    body = json.loads(private_path(directory / (segment + ".json")).read_bytes())
    require(
        body.get("format") == "claimcore-wal-copy-1"
        and body.get("cluster") == cluster
        and body.get("segment") == segment,
        "wal-sidecar-invalid",
    )
    return ciphertext, body


def _check_copy_digests(config: JsonObject, ciphertext: Path, record: JsonObject) -> None:
    require(record.get("ciphertextSha256") == digest(ciphertext), "wal-ciphertext-digest")
    require(record.get("sourceSha256") == plaintext_digest(config, ciphertext), "wal-source-digest")


def _attestation_paths(config: JsonObject, segment: str, cluster: str) -> list[Path]:
    return list(
        islice(
            config["inventoryRoot"].glob("wal." + segment + "." + cluster + ".*.json"),
            DUPLICATE_PROBE,
        )
    )


def attest_wal(config: JsonObject, cluster: str, segment: str) -> None:
    """Locally attest one archived WAL copy after proving its bytes and identity."""
    require(re.fullmatch(WAL_NAME, segment) is not None, "wal-name")
    archive = private_path(config["archiveRoot"] / "wal" / cluster, directory=True)
    ciphertext, record = _sidecar(archive, segment, cluster)
    _check_copy_digests(config, ciphertext, record)
    system_id, timeline, segment_bytes = postgres_control(config, cluster)
    parse_wal(config, ciphertext, segment, segment_bytes)
    require(
        len(_attestation_paths(config, segment, cluster)) == 0, "wal-attestation-already-exists"
    )
    witness = metadata(config, "witness")
    cluster_identity = metadata(config, cluster)
    require(cluster_identity[:IDENTITY_PREFIX] == witness[:IDENTITY_PREFIX], "wal-cluster-identity")
    require(int(segment[:TIMELINE_DIGITS], 16) == timeline, "wal-timeline-mismatch")
    attestation = inventory.wal_copy(
        inventory.CopySource(config, cluster, witness, digest),
        segment,
        ciphertext,
        record,
        system_id,
        segment_bytes=segment_bytes,
    )
    save_attestation(config, attestation)
    sys.stdout.write(
        (
            json.dumps(
                {
                    "status": "local-wal-attested",
                    "copyId": attestation["copyId"],
                    "primaryRegistered": False,
                    "freshnessQualified": False,
                    "deletionProved": False,
                }
            )
        )
        + "\n"
    )


def wal_number(segment: str, segment_bytes: int) -> int:
    """Return the absolute segment number encoded in a WAL segment name."""
    segments_per_log = (1 << LOG_SHIFT) // segment_bytes
    log = int(segment[TIMELINE_DIGITS:LOG_END], 16)
    slot = int(segment[LOG_END:SEGMENT_NAME_LENGTH], 16)
    require(slot < segments_per_log, "wal-segment-number")
    return log * segments_per_log + slot


def bounded_names(paths: Iterable[Path], suffix: str, limit: int) -> set[str]:
    """Collect at most `limit` distinct names with `suffix` removed."""
    names: set[str] = set()
    for path in paths:
        require(len(names) < limit, "wal-inventory-limit")
        names.add(path.name.removesuffix(suffix))
    return names


@dataclass(frozen=True)
class _Expectation:
    """What a WAL attestation must agree with."""

    cluster: str
    segment: str
    ciphertext_bytes: int
    ciphertext_sha256: str
    tip: JsonObject


def _check_attestation(
    config: JsonObject, attestation: JsonObject, expected: _Expectation, base: JsonObject
) -> None:
    require(
        attestation.get("cluster") == expected.cluster
        and attestation.get("kind") == "WAL"
        and attestation.get("walSegment") == expected.segment,
        "wal-attestation-identity",
    )
    require(
        attestation.get("state") == "UNVERIFIED"
        and attestation.get("primaryRegistration") == "NOT_REGISTERED",
        "wal-attestation-state",
    )
    tip = expected.tip
    require(
        attestation.get("installationId") == tip["installationId"]
        and attestation.get("lineageId") == tip["lineageId"]
        and attestation.get("epoch") == tip["epoch"],
        "wal-attestation-installation",
    )
    require(
        attestation.get("ciphertextSha256") == expected.ciphertext_sha256
        and attestation.get("ciphertextBytes") == expected.ciphertext_bytes,
        "wal-attestation-ciphertext",
    )
    require(attestation.get("postgresSystemId") == base["postgresSystemId"], "wal-system-mismatch")
    require(attestation.get("walSegmentBytes") == base["walSegmentBytes"], "wal-size-mismatch")
    require(
        attestation.get("signingKeyId") == config["signingKeyId"]
        and attestation.get("encryptionKeyId") == config["encryptionKeyId"],
        "wal-key-identity",
    )


def _verify_segment(
    config: JsonObject,
    directory: Path,
    segment: str,
    cluster: str,
    context: tuple[JsonObject, JsonObject],
) -> tuple[str, int | None, JsonObject]:
    """Verify one archived segment; return its attestation file, timeline and attestation."""
    base, tip = context
    require(re.fullmatch(WAL_NAME, segment) is not None, "wal-name")
    ciphertext, sidecar_body = _sidecar(directory, segment, cluster)
    _check_copy_digests(config, ciphertext, sidecar_body)
    parse_wal(config, ciphertext, segment, base["walSegmentBytes"])
    records = _attestation_paths(config, segment, cluster)
    require(len(records) == 1, "wal-attestation-missing-or-duplicate")
    attestation_path = private_path(records[0])
    verify_signature(config, attestation_path, attestation_path.with_suffix(".sig"))
    attestation = json.loads(attestation_path.read_bytes())
    require(
        attestation_path.read_bytes() == inventory.canonical(attestation),
        "attestation-not-canonical",
    )
    expected = _Expectation(
        cluster, segment, ciphertext.stat().st_size, sidecar_body["ciphertextSha256"], tip
    )
    _check_attestation(config, attestation, expected, base)
    timeline = None
    if len(segment) == SEGMENT_NAME_LENGTH:
        timeline = int(segment[:TIMELINE_DIGITS], 16)
        require(attestation.get("timeline") == timeline, "wal-timeline-mismatch")
    return attestation_path.name, timeline, attestation


def _verify_cluster_wal(
    config: JsonObject, cluster: str, context: tuple[JsonObject, JsonObject], seen: set[str]
) -> int:
    directory = config["archiveRoot"] / "wal" / cluster
    if not directory.exists():
        return 0
    directory = private_path(directory, directory=True)
    ciphertext_names = bounded_names(directory.glob("*.age"), ".age", config["maxWalCopies"])
    sidecar_names = bounded_names(directory.glob("*.json"), ".json", config["maxWalCopies"])
    require(ciphertext_names == sidecar_names, "wal-copy-missing")
    timelines: dict[int, list[int]] = {}
    for count, segment in enumerate(sorted(ciphertext_names), start=1):
        require(count <= config["maxWalCopies"], "wal-inventory-limit")
        name, timeline, attestation = _verify_segment(config, directory, segment, cluster, context)
        seen.add(name)
        if timeline is not None:
            numbers = timelines.setdefault(timeline, [])
            numbers.append(wal_number(segment, attestation["walSegmentBytes"]))
    for numbers in timelines.values():
        numbers.sort()
        require(all(right == left + 1 for left, right in pairwise(numbers)), "wal-segment-gap")
    return len(ciphertext_names)


def verify_wal_inventory(
    config: JsonObject, base_attestations: dict[str, JsonObject], tip: JsonObject
) -> int:
    """Verify every archived WAL copy against its attestation; return how many there are."""
    seen: set[str] = set()
    count = 0
    for cluster in CLUSTERS:
        count += _verify_cluster_wal(config, cluster, (base_attestations[cluster], tip), seen)
        require(count <= config["maxWalCopies"], "wal-inventory-limit")
    signed_wal = bounded_names(
        config["inventoryRoot"].glob("wal.*.json"), "", config["maxWalCopies"]
    )
    require(signed_wal == seen, "orphan-wal-attestation")
    return count
