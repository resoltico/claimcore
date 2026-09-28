#!/usr/bin/env python3
"""Owner-only encrypted PostgreSQL backup and quarantine qualification.

This program never promotes a restored cluster. Its output is a private,
machine-readable qualification input, not authority to admit case work.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import uuid
from datetime import datetime, timezone
from itertools import islice, pairwise
from pathlib import Path

sys.dont_write_bytecode = True
import inventory
import promotion
from checkpoint_signer_policy import socket_directory
from pg_service_policy import validate as validate_pg_service
from tool_versions import locate as locate_tool
from tool_versions import matches as matches_tool_version

ROOT = Path(__file__).resolve().parents[2]
CLUSTERS = ("primary", "witness")
IDENTITY_SQL = {
    "primary": "SELECT installation_id::text, lineage_id::text, witness_epoch::text FROM claimcore.installation_lineage WHERE singleton",
    "witness": "SELECT installation_id::text, lineage_id::text, epoch::text, tip_sequence::text, encode(tip_hash, 'hex') FROM claimcore_witness.installation WHERE singleton",
}


class BackupFailure(Exception):
    pass


def require(condition, category):
    if not condition:
        raise BackupFailure(category)


def private_path(path, directory=False):
    path = Path(os.path.abspath(path))
    for component in (path, *path.parents):
        require(
            not stat.S_ISLNK(os.lstat(component).st_mode), "linked-private-path-refused"
        )
    path = path.resolve(strict=True)
    require(not path.is_relative_to(ROOT), "repository-path-refused")
    mode = path.stat().st_mode & 0o777
    require(mode & 0o077 == 0, "private-permissions-required")
    require(path.is_dir() if directory else path.is_file(), "private-path-type")
    return path


def configuration(path, archiver=False):
    config_path = private_path(path)
    descriptor = os.open(config_path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, "rb") as stream:
        config = json.load(stream)
    require(
        config.get("format") == "claimcore-managed-backup-1", "configuration-format"
    )
    require(
        "qualifiedVerifierSha256" not in config,
        "owner-selected-verifier-is-unsupported",
    )
    archive = private_path(config["archiveRoot"], directory=True)
    recipient = config["ageRecipient"]
    require(
        re.fullmatch(r"age1[023456789acdefghjklmnpqrstuvwxyz]{58}", recipient)
        is not None,
        "recipient-format",
    )
    config["archiveRoot"] = archive
    if archiver:
        require(
            set(config) == {"format", "archiveRoot", "ageRecipient"},
            "archiver-config-has-owner-material",
        )
        return config
    checkpoint = private_path(config["checkpointRoot"], directory=True)
    require(
        not checkpoint.is_relative_to(archive)
        and not archive.is_relative_to(checkpoint),
        "checkpoint-storage-not-separate",
    )
    config["checkpointRoot"] = checkpoint
    inventory_root = private_path(config["inventoryRoot"], directory=True)
    require(
        not inventory_root.is_relative_to(archive)
        and not archive.is_relative_to(inventory_root),
        "inventory-storage-not-separate",
    )
    config["inventoryRoot"] = inventory_root
    config["commitmentKey"] = private_path(config["commitmentKey"])
    require(32 <= config["commitmentKey"].stat().st_size <= 4096, "commitment-key-size")
    for name in ("signingKeyId", "checkpointSigningKeyId", "encryptionKeyId"):
        uuid.UUID(config[name])
    require(
        config["signingKeyId"] != config["checkpointSigningKeyId"],
        "checkpoint-signer-separation",
    )
    for name in (
        "backupIntervalSeconds",
        "maximumBackupAgeSeconds",
        "restoreHorizonSeconds",
    ):
        require(
            type(config[name]) is int and 60 <= config[name] <= 10 * 365 * 86400,
            "backup-cadence-policy",
        )
    for name in (
        "backupRetentionSeconds",
        "walRetentionSeconds",
        "checkpointRetentionSeconds",
    ):
        require(
            type(config[name]) is int and 86400 <= config[name] <= 10 * 365 * 86400,
            "retention-policy",
        )
    require(
        config["backupIntervalSeconds"]
        <= config["maximumBackupAgeSeconds"]
        <= config["restoreHorizonSeconds"],
        "backup-cadence-policy",
    )
    require(
        config["restoreHorizonSeconds"]
        <= min(config["backupRetentionSeconds"], config["walRetentionSeconds"]),
        "recovery-retention-policy",
    )
    require(
        config["checkpointRetentionSeconds"]
        > max(config["backupRetentionSeconds"], config["walRetentionSeconds"]),
        "checkpoint-retention-policy",
    )
    max_backup = config["maxBackupBytes"]
    max_entries = config["maxTarEntries"]
    max_wal = config["maxWalCopies"]
    require(
        type(max_backup) is int and 1024 * 1024 <= max_backup <= 1024**4,
        "backup-size-policy",
    )
    require(
        type(max_entries) is int and 1 <= max_entries <= 10_000_000,
        "backup-entry-policy",
    )
    require(type(max_wal) is int and 1 <= max_wal <= 100_000, "wal-inventory-policy")
    config["signingKey"] = private_path(config["signingKey"])
    config["verificationKey"] = private_path(config["verificationKey"])
    config["checkpointVerificationKey"] = private_path(
        config["checkpointVerificationKey"]
    )
    mode = config["checkpointSignerMode"]
    require(mode in ("LOCAL_SYNTHETIC", "REMOTE_SSH"), "checkpoint-signer-mode")
    if mode == "LOCAL_SYNTHETIC":
        require(config.get("checkpointSignerRemote") is None, "checkpoint-signer-mode")
        signer_socket = Path(config["checkpointSignerSocket"])
        require(signer_socket.is_absolute(), "checkpoint-signer-endpoint")
        socket_directory(signer_socket.parent)
        config["checkpointSignerSocket"] = signer_socket
    else:
        require(config.get("checkpointSignerSocket") is None, "checkpoint-signer-mode")
        remote = config["checkpointSignerRemote"]
        require(
            isinstance(remote, dict)
            and set(remote)
            == {
                "topologyFile",
                "topologySignatureFile",
                "knownHostsFile",
                "sshIdentityFile",
            },
            "checkpoint-remote-config",
        )
        for name, target in (
            ("topologyFile", "checkpointSignerTopologyFile"),
            ("topologySignatureFile", "checkpointSignerTopologySignatureFile"),
            ("knownHostsFile", "checkpointKnownHostsFile"),
            ("sshIdentityFile", "checkpointSshIdentityFile"),
        ):
            config[target] = private_path(remote[name])
    require(
        config["verificationKey"].read_bytes()
        != config["checkpointVerificationKey"].read_bytes(),
        "checkpoint-signer-separation",
    )
    require(
        isinstance(config["checkpointCustodianId"], str)
        and re.fullmatch(
            r"[A-Za-z][A-Za-z0-9_-]{0,63}", config["checkpointCustodianId"]
        ),
        "checkpoint-custodian",
    )
    for cluster in CLUSTERS:
        item = config[cluster]
        require(
            re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,63}", item["custodianId"])
            is not None,
            "custodian-id",
        )
        for name in ("metadataService", "replicationService"):
            require(
                re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,63}", item[name]) is not None,
                "service-name",
            )
        require(
            config["checkpointCustodianId"] != item["custodianId"],
            "checkpoint-custodian-separation",
        )
    validate_pg_service(config)
    return config


def tool(name, version_prefix=None):
    found = locate_tool(name)
    require(found is not None, "missing-tool-" + name)
    if version_prefix:
        result = subprocess.run([found, "--version"], capture_output=True, check=False)
        require(
            result.returncode == 0
            and matches_tool_version(
                name, version_prefix, result.stdout.decode("utf-8", "replace")
            ),
            "wrong-tool-version-" + name,
        )
    return found


def pg_env(service):
    # Libpq gains environment options over time; only the admitted service file
    # and exact service name may control a capture connection.
    environment = {
        name: value for name, value in os.environ.items() if not name.startswith("PG")
    }
    environment["PGSERVICEFILE"] = os.environ["PGSERVICEFILE"]
    environment["PGSERVICE"] = service
    environment["PGCONNECT_TIMEOUT"] = "10"
    return environment


def metadata(config, cluster):
    psql = tool("psql", "psql (PostgreSQL) 18.6")
    result = subprocess.run(
        [
            psql,
            "-X",
            "-w",
            "-q",
            "-A",
            "-t",
            "-F",
            "\t",
            "-v",
            "ON_ERROR_STOP=1",
            "-c",
            IDENTITY_SQL[cluster],
        ],
        env=pg_env(config[cluster]["metadataService"]),
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "metadata-read-failed")
    lines = result.stdout.decode("ascii", "strict").splitlines()
    expected = 3 if cluster == "primary" else 5
    require(len(lines) == 1 and len(lines[0].split("\t")) == expected, "metadata-shape")
    fields = lines[0].split("\t")
    for value in fields[:2]:
        uuid.UUID(value)
    require(int(fields[2]) > 0, "metadata-epoch")
    if cluster == "witness":
        require(
            int(fields[3]) >= 0
            and re.fullmatch(r"[0-9a-f]{64}", fields[4]) is not None,
            "metadata-tip",
        )
    return fields


def digest(path):
    sha = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def sync_file(path):
    with Path(path).open("rb") as stream:
        os.fsync(stream.fileno())


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def json_bytes(value):
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")


def utc_timestamp(value):
    require(
        isinstance(value, str)
        and re.fullmatch(
            r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value
        )
        is not None,
        "timestamp-format",
    )
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def write_new(path, payload):
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def sign_with_key(key, source, signature):
    temporary = signature.with_name("." + signature.name + "." + str(uuid.uuid4()))
    try:
        result = subprocess.run(
            [
                tool("openssl"),
                "pkeyutl",
                "-sign",
                "-rawin",
                "-inkey",
                str(key),
                "-in",
                str(source),
                "-out",
                str(temporary),
            ],
            capture_output=True,
            check=False,
        )
        require(result.returncode == 0, "signing-failed")
        require(temporary.stat().st_size == 64, "signature-length")
        os.link(temporary, signature, follow_symlinks=False)
    finally:
        temporary.unlink(missing_ok=True)
    sync_file(signature)
    sync_directory(signature.parent)


def sign(config, source, signature):
    sign_with_key(config["signingKey"], source, signature)


def verify_with_key(key, source, signature):
    source = private_path(source)
    signature = private_path(signature)
    require(signature.stat().st_size == 64, "signature-length")
    result = subprocess.run(
        [
            tool("openssl"),
            "pkeyutl",
            "-verify",
            "-rawin",
            "-pubin",
            "-inkey",
            str(key),
            "-in",
            str(source),
            "-sigfile",
            str(signature),
        ],
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "signature-invalid")


def verify_signature(config, source, signature):
    verify_with_key(config["verificationKey"], source, signature)


def verify_checkpoint_signature(config, source, signature):
    verify_with_key(config["checkpointVerificationKey"], source, signature)


def capture_one(config, cluster, cycle):
    before = metadata(config, cluster)
    output = cycle / (cluster + ".tar.age")
    with output.open("xb") as sealed:
        age = subprocess.Popen(
            [tool("age", "v1.3.2"), "--encrypt", "--recipient", config["ageRecipient"]],
            stdin=subprocess.PIPE,
            stdout=sealed,
            stderr=subprocess.DEVNULL,
        )
        pg = subprocess.Popen(
            [
                tool("pg_basebackup", "pg_basebackup (PostgreSQL) 18.6"),
                "--pgdata=-",
                "--format=tar",
                "--wal-method=fetch",
                "--manifest-checksums=SHA256",
                "--no-password",
                "--checkpoint=fast",
            ],
            env=pg_env(config[cluster]["replicationService"]),
            stdout=age.stdin,
            stderr=subprocess.PIPE,
        )
        age.stdin.close()
        _, pg_error = pg.communicate()
        pg_status = pg.returncode
        age_status = age.wait()
        sealed.flush()
        os.fsync(sealed.fileno())
    if pg_status != 0:
        category = "basebackup-source-failed"
        for fragment, label in (
            (b"pg_hba.conf", "hba"),
            (b"password authentication failed", "authentication"),
            (b"no password supplied", "authentication"),
            (b"must be superuser or replication role", "replication-role"),
            (b"permission denied", "permission"),
            (b"replication slot", "replication-slot"),
            (b"replication privileges", "replication-privileges"),
            (b"manifest", "manifest"),
            (b"stdout", "stdout"),
            (b"WAL", "wal"),
            (b"replication connection", "replication-connection"),
            (b"connection", "connection"),
            (b"replication", "replication"),
        ):
            if fragment in pg_error:
                category += "-" + label
                break
        raise BackupFailure(category)
    require(age_status == 0, "basebackup-encryption-failed")
    sync_directory(cycle)
    after = metadata(config, cluster)
    require(before[:3] == after[:3], "identity-changed-during-backup")
    require(before[:2] == after[:2], "lineage-changed-during-backup")
    return {
        "before": before,
        "after": after,
        "ciphertextSha256": digest(output),
        "ciphertextBytes": output.stat().st_size,
    }


def read_pg_manifest(config, ciphertext):
    require(ciphertext.stat().st_size <= config["maxBackupBytes"], "backup-size-policy")
    age = subprocess.Popen(
        [
            tool("age", "v1.3.2"),
            "--decrypt",
            "--identity",
            str(private_path(config["ageIdentity"])),
            str(ciphertext),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    manifest_bytes = None
    try:
        with tarfile.open(fileobj=age.stdout, mode="r|") as archive:
            expanded = 0
            for count, member in enumerate(archive, start=1):
                expanded += member.size
                require(
                    count <= config["maxTarEntries"]
                    and expanded <= config["maxBackupBytes"],
                    "backup-expansion-limit",
                )
                if member.name.removeprefix("./") == "backup_manifest":
                    require(
                        manifest_bytes is None
                        and member.isfile()
                        and member.size <= 32 * 1024 * 1024,
                        "backup-manifest-shape",
                    )
                    manifest_bytes = archive.extractfile(member).read(member.size + 1)
                    require(len(manifest_bytes) == member.size, "backup-manifest-short")
        require(
            age.wait() == 0 and manifest_bytes is not None, "backup-manifest-missing"
        )
    finally:
        if age.poll() is None:
            age.kill()
            age.wait()
    manifest = json.loads(manifest_bytes)
    manifest["_sha256"] = hashlib.sha256(manifest_bytes).hexdigest()
    return manifest


def save_attestation(config, attestation):
    copy_id = attestation["copyId"]
    uuid.UUID(copy_id)
    prefix = attestation["cycleId"] or ("wal." + attestation["walSegment"])
    target = config["inventoryRoot"] / (
        prefix + "." + attestation["cluster"] + "." + copy_id + ".json"
    )
    write_new(target, inventory.canonical(attestation))
    try:
        sync_directory(config["inventoryRoot"])
        sign(config, target, target.with_suffix(".sig"))
    except Exception:
        target.unlink(missing_ok=True)
        raise
    return target


def postgres_control(config, cluster):
    result = subprocess.run(
        [
            tool("psql", "psql (PostgreSQL) 18.6"),
            "-X",
            "-w",
            "-q",
            "-A",
            "-t",
            "-F",
            "\t",
            "-v",
            "ON_ERROR_STOP=1",
            "-c",
            "SELECT s.system_identifier::text, c.timeline_id::text, i.bytes_per_wal_segment::text FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c CROSS JOIN pg_control_init() i",
        ],
        env=pg_env(config[cluster]["metadataService"]),
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "postgres-system-read-failed")
    parts = result.stdout.decode("ascii", "strict").strip().split("\t")
    require(
        len(parts) == 3 and re.fullmatch(r"[0-9]{1,20}", parts[0]) is not None,
        "postgres-system-shape",
    )
    timeline = int(parts[1])
    segment_bytes = int(parts[2])
    require(
        timeline > 0
        and segment_bytes >= 1024 * 1024
        and segment_bytes <= 1024 * 1024 * 1024
        and segment_bytes & (segment_bytes - 1) == 0,
        "postgres-wal-control",
    )
    return parts[0], timeline, segment_bytes


def plaintext_digest(config, ciphertext):
    age = subprocess.Popen(
        [
            tool("age", "v1.3.2"),
            "--decrypt",
            "--identity",
            str(private_path(config["ageIdentity"])),
            str(ciphertext),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    sha = hashlib.sha256()
    total = 0
    for block in iter(lambda: age.stdout.read(1024 * 1024), b""):
        total += len(block)
        if total > config["maxBackupBytes"]:
            age.kill()
            age.wait()
            raise BackupFailure("wal-decryption-limit")
        sha.update(block)
    require(age.wait() == 0, "wal-decryption-failed")
    return sha.hexdigest()


def parse_wal(config, ciphertext, segment, segment_bytes):
    with tempfile.TemporaryDirectory(prefix="claimcore-wal-check-") as temporary:
        work = Path(temporary)
        work.chmod(0o700)
        source = work / segment
        age = subprocess.Popen(
            [
                tool("age", "v1.3.2"),
                "--decrypt",
                "--identity",
                str(private_path(config["ageIdentity"])),
                str(ciphertext),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
        total = 0
        with source.open("xb") as output:
            for block in iter(lambda: age.stdout.read(1024 * 1024), b""):
                total += len(block)
                if total > config["maxBackupBytes"]:
                    age.kill()
                    age.wait()
                    raise BackupFailure("wal-decryption-limit")
                output.write(block)
        require(age.wait() == 0, "wal-decryption-failed")
        if segment.endswith(".history"):
            require(total <= 64 * 1024, "timeline-history-size")
            lines = source.read_text("utf-8").splitlines()
            require(
                any(
                    re.fullmatch(r"[0-9]+\s+[0-9A-F]+/[0-9A-F]+\s+.+", line)
                    for line in lines
                    if not line.startswith("#")
                ),
                "timeline-history-format",
            )
        else:
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


def attest_wal(config, cluster, segment):
    require(
        re.fullmatch(r"(?:[0-9A-F]{24}|[0-9A-F]{8}\.history)", segment) is not None,
        "wal-name",
    )
    archive = private_path(config["archiveRoot"] / "wal" / cluster, directory=True)
    ciphertext = private_path(archive / (segment + ".age"))
    sidecar = private_path(archive / (segment + ".json"))
    record = json.loads(sidecar.read_bytes())
    require(
        record.get("format") == "claimcore-wal-copy-1"
        and record.get("cluster") == cluster
        and record.get("segment") == segment,
        "wal-sidecar-invalid",
    )
    require(
        record.get("ciphertextSha256") == digest(ciphertext), "wal-ciphertext-digest"
    )
    require(
        record.get("sourceSha256") == plaintext_digest(config, ciphertext),
        "wal-source-digest",
    )
    system_id, timeline, segment_bytes = postgres_control(config, cluster)
    parse_wal(config, ciphertext, segment, segment_bytes)
    existing = list(
        islice(
            config["inventoryRoot"].glob("wal." + segment + "." + cluster + ".*.json"),
            2,
        )
    )
    require(len(existing) == 0, "wal-attestation-already-exists")
    witness = metadata(config, "witness")
    cluster_identity = metadata(config, cluster)
    require(cluster_identity[:3] == witness[:3], "wal-cluster-identity")
    require(int(segment[:8], 16) == timeline, "wal-timeline-mismatch")
    attestation = inventory.wal_copy(
        config,
        cluster,
        segment,
        ciphertext,
        record,
        system_id,
        segment_bytes,
        witness,
        digest,
    )
    save_attestation(config, attestation)
    print(
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


def wal_number(segment, segment_bytes):
    segments_per_log = (1 << 32) // segment_bytes
    log = int(segment[8:16], 16)
    slot = int(segment[16:24], 16)
    require(slot < segments_per_log, "wal-segment-number")
    return log * segments_per_log + slot


def bounded_names(paths, suffix, limit):
    names = set()
    for path in paths:
        require(len(names) < limit, "wal-inventory-limit")
        names.add(path.name.removesuffix(suffix))
    return names


def verify_wal_inventory(config, base_attestations, tip):
    seen = set()
    count = 0
    for cluster in CLUSTERS:
        directory = config["archiveRoot"] / "wal" / cluster
        if not directory.exists():
            continue
        directory = private_path(directory, directory=True)
        ciphertext_names = bounded_names(
            directory.glob("*.age"), ".age", config["maxWalCopies"]
        )
        sidecar_names = bounded_names(
            directory.glob("*.json"), ".json", config["maxWalCopies"]
        )
        require(ciphertext_names == sidecar_names, "wal-copy-missing")
        timelines = {}
        for segment in sorted(ciphertext_names):
            count += 1
            require(count <= config["maxWalCopies"], "wal-inventory-limit")
            require(
                re.fullmatch(r"(?:[0-9A-F]{24}|[0-9A-F]{8}\.history)", segment)
                is not None,
                "wal-name",
            )
            ciphertext = private_path(directory / (segment + ".age"))
            sidecar = private_path(directory / (segment + ".json"))
            sidecar_body = json.loads(sidecar.read_bytes())
            require(
                sidecar_body.get("format") == "claimcore-wal-copy-1"
                and sidecar_body.get("cluster") == cluster
                and sidecar_body.get("segment") == segment,
                "wal-sidecar-invalid",
            )
            require(
                sidecar_body.get("ciphertextSha256") == digest(ciphertext),
                "wal-ciphertext-digest",
            )
            require(
                sidecar_body.get("sourceSha256")
                == plaintext_digest(config, ciphertext),
                "wal-source-digest",
            )
            parse_wal(
                config,
                ciphertext,
                segment,
                base_attestations[cluster]["walSegmentBytes"],
            )
            records = list(
                islice(
                    config["inventoryRoot"].glob(
                        "wal." + segment + "." + cluster + ".*.json"
                    ),
                    2,
                )
            )
            require(len(records) == 1, "wal-attestation-missing-or-duplicate")
            attestation_path = private_path(records[0])
            verify_signature(
                config, attestation_path, attestation_path.with_suffix(".sig")
            )
            attestation = json.loads(attestation_path.read_bytes())
            require(
                attestation_path.read_bytes() == inventory.canonical(attestation),
                "attestation-not-canonical",
            )
            require(
                attestation.get("cluster") == cluster
                and attestation.get("kind") == "WAL"
                and attestation.get("walSegment") == segment,
                "wal-attestation-identity",
            )
            require(
                attestation.get("state") == "UNVERIFIED"
                and attestation.get("primaryRegistration") == "NOT_REGISTERED",
                "wal-attestation-state",
            )
            require(
                attestation.get("installationId") == tip["installationId"]
                and attestation.get("lineageId") == tip["lineageId"]
                and attestation.get("epoch") == tip["epoch"],
                "wal-attestation-installation",
            )
            require(
                attestation.get("ciphertextSha256") == sidecar_body["ciphertextSha256"]
                and attestation.get("ciphertextBytes") == ciphertext.stat().st_size,
                "wal-attestation-ciphertext",
            )
            require(
                attestation.get("postgresSystemId")
                == base_attestations[cluster]["postgresSystemId"],
                "wal-system-mismatch",
            )
            require(
                attestation.get("walSegmentBytes")
                == base_attestations[cluster]["walSegmentBytes"],
                "wal-size-mismatch",
            )
            require(
                attestation.get("signingKeyId") == config["signingKeyId"]
                and attestation.get("encryptionKeyId") == config["encryptionKeyId"],
                "wal-key-identity",
            )
            seen.add(attestation_path.name)
            if len(segment) == 24:
                timeline = int(segment[:8], 16)
                require(
                    attestation.get("timeline") == timeline, "wal-timeline-mismatch"
                )
                timelines.setdefault(timeline, []).append(
                    wal_number(segment, attestation["walSegmentBytes"])
                )
        for numbers in timelines.values():
            numbers.sort()
            require(
                all(right == left + 1 for left, right in pairwise(numbers)),
                "wal-segment-gap",
            )
    signed_wal = bounded_names(
        config["inventoryRoot"].glob("wal.*.json"), "", config["maxWalCopies"]
    )
    require(signed_wal == seen, "orphan-wal-attestation")
    return count


def capture(config, held=None):
    archive = config["archiveRoot"]
    if held is None:
        cycle_id = str(uuid.uuid4())
        cycle = archive / cycle_id
        cycle.mkdir(mode=0o700)
    else:
        cycle = private_path(held["cycleRoot"], directory=True)
        require(cycle.parent == archive and not any(cycle.iterdir()), "held-cycle-root")
        cycle_id = cycle.name
        require(str(uuid.UUID(cycle_id)) == cycle_id, "held-cycle-id")
    new_external = []
    try:
        details = {cluster: capture_one(config, cluster, cycle) for cluster in CLUSTERS}
        primary = details["primary"]["after"]
        witness = details["witness"]["after"]
        for index, name in enumerate(("installation", "lineage", "epoch")):
            require(
                primary[index] == witness[index],
                "cluster-identity-mismatch-" + name,
            )
        if held is not None:
            require(
                primary[0] == held["installationId"]
                and primary[1] == held["lineageId"]
                and int(primary[2]) == held["epoch"]
                and int(witness[3]) == held["cutoffSequence"]
                and witness[4] == held["cutoffHash"],
                "held-capture-cutoff",
            )
        attestations = {}
        for cluster in CLUSTERS:
            ciphertext = cycle / (cluster + ".tar.age")
            pg_manifest = read_pg_manifest(config, ciphertext)
            system_id, timeline, segment_bytes = postgres_control(config, cluster)
            require(
                str(pg_manifest.get("System-Identifier")) == system_id,
                "backup-system-changed",
            )
            require(
                pg_manifest["WAL-Ranges"][0]["Timeline"] == timeline,
                "backup-timeline-changed",
            )
            if held is not None:
                require(
                    system_id == held[cluster + "SystemId"]
                    and timeline == held[cluster + "Timeline"],
                    "held-cluster-identity",
                )
            attestations[cluster] = inventory.base_copy(
                config,
                cluster,
                cycle_id,
                ciphertext,
                pg_manifest,
                segment_bytes,
                witness,
                digest,
            )
        captured_at = inventory.utc_time(datetime.now(timezone.utc))
        barrier_fields = {
            "leaseId": held["leaseId"] if held is not None else None,
            "captureNonce": held["nonce"] if held is not None else None,
            "writerGeneration": held["writerGeneration"] if held is not None else None,
            "backupCaptureSequence": held["cutoffSequence"]
            if held is not None
            else None,
            "backupCaptureHash": held["cutoffHash"] if held is not None else None,
            "maintenanceEvidenceSha256": held["maintenanceEvidenceSha256"]
            if held is not None
            else None,
        }
        checkpoint = config["checkpointRoot"]
        checkpoint_file = checkpoint / (cycle_id + ".json")
        checkpoint_body = {
            "format": "claimcore-witness-checkpoint-1",
            "cycleId": cycle_id,
            "installationId": primary[0],
            "lineageId": primary[1],
            "epoch": int(primary[2]),
            "sequence": int(witness[3]),
            "hash": witness[4],
            "capturedAt": captured_at,
            "checkpointSigningKeyId": config["checkpointSigningKeyId"],
            "checkpointCustodianCommitment": inventory.commitment(
                config["commitmentKey"].read_bytes(),
                "custodian",
                config["checkpointCustodianId"],
            ),
            **barrier_fields,
        }
        write_new(checkpoint_file, json_bytes(checkpoint_body))
        new_external.append(checkpoint_file)
        sync_directory(checkpoint)
        from checkpoint_signer_client import sign_checkpoint

        sign_checkpoint(config, checkpoint_file, checkpoint_file.with_suffix(".sig"))
        new_external.append(checkpoint_file.with_suffix(".sig"))
        base_fields = (
            "copyId",
            "eventId",
            "postgresSystemId",
            "timeline",
            "walSegmentBytes",
            "backupManifestSha256",
            "walStartLsn",
            "walEndLsn",
            "ciphertextSha256",
            "ciphertextBytes",
            "locationCommitment",
            "custodianCommitment",
        )
        manifest = {
            "format": "claimcore-backup-cycle-1",
            "cycleId": cycle_id,
            "consistencyScope": "unfenced-capture",
            "capturedAt": captured_at,
            "installationId": primary[0],
            "lineageId": primary[1],
            "epoch": int(primary[2]),
            "primary": details["primary"],
            "witness": details["witness"],
            "witnessCheckpoint": {"sequence": int(witness[3]), "hash": witness[4]},
            "copyIds": {
                cluster: attestations[cluster]["copyId"] for cluster in CLUSTERS
            },
            "baseCopies": {
                cluster: {name: attestations[cluster][name] for name in base_fields}
                for cluster in CLUSTERS
            },
            "copySigningKeyId": config["signingKeyId"],
            "checkpointSigningKeyId": config["checkpointSigningKeyId"],
            "checkpointSha256": digest(checkpoint_file),
            **barrier_fields,
        }
        write_new(cycle / "manifest.json", json_bytes(manifest))
        sync_directory(cycle)
        sign(config, cycle / "manifest.json", cycle / "manifest.sig")
        # Distinct local files/keys do not prove independent custody.
        # The owner receipt and post-capture REGISTER/VERIFY are separate.
        for cluster in CLUSTERS:
            saved = save_attestation(config, attestations[cluster])
            new_external.extend((saved, saved.with_suffix(".sig")))
        if held is None:
            print(json.dumps({"status": "captured", "cycleId": cycle_id}))
            return None
        return {
            "primaryCiphertextPath": str(cycle / "primary.tar.age"),
            "witnessCiphertextPath": str(cycle / "witness.tar.age"),
            "checkpointPath": str(checkpoint_file),
            "cycleManifestPath": str(cycle / "manifest.json"),
            "cycleManifestSignaturePath": str(cycle / "manifest.sig"),
        }
    except Exception:
        # Preserve held-cycle material for owner reconciliation; unfenced fresh
        # scratch may be removed only before any owner FINISH was attempted.
        if held is None:
            for item in reversed(new_external):
                item.unlink(missing_ok=True)
            shutil.rmtree(cycle)
        raise


def safe_extract(source, destination, max_bytes, max_entries):
    with tarfile.open(source, mode="r:") as archive:
        expanded = 0
        for count, member in enumerate(archive, start=1):
            require(0 <= member.size <= max_bytes, "backup-member-size")
            expanded += member.size
            require(
                count <= max_entries and expanded <= max_bytes, "backup-expansion-limit"
            )
            name = Path(member.name)
            require(
                not name.is_absolute() and ".." not in name.parts,
                "unsafe-backup-member",
            )
            require(member.isfile() or member.isdir(), "unsupported-backup-member")
            target = destination / name
            require(
                target.resolve().is_relative_to(destination.resolve()),
                "unsafe-backup-member",
            )
        archive.extractall(destination, filter="data")


def inspect(config, cycle_id, verifier):
    uuid.UUID(cycle_id)
    cycle = private_path(config["archiveRoot"] / cycle_id, directory=True)
    source = cycle / "manifest.json"
    verify_signature(config, source, cycle / "manifest.sig")
    require(source.stat().st_size <= 1024 * 1024, "manifest-size")
    manifest = json.loads(source.read_bytes())
    require(
        manifest.get("format") == "claimcore-backup-cycle-1"
        and manifest.get("cycleId") == cycle_id,
        "manifest-invalid",
    )
    captured_at = utc_timestamp(manifest.get("capturedAt"))
    age_seconds = (datetime.now(timezone.utc) - captured_at).total_seconds()
    require(
        -300 <= age_seconds <= config["maximumBackupAgeSeconds"], "backup-age-policy"
    )
    checkpoint = config["checkpointRoot"] / (cycle_id + ".json")
    verify_checkpoint_signature(config, checkpoint, checkpoint.with_suffix(".sig"))
    require(checkpoint.stat().st_size <= 16 * 1024, "checkpoint-size")
    require(
        digest(checkpoint) == manifest["checkpointSha256"], "checkpoint-manifest-digest"
    )
    tip = json.loads(checkpoint.read_bytes())
    require(tip.get("format") == "claimcore-witness-checkpoint-1", "checkpoint-format")
    require(
        manifest.get("copySigningKeyId") == config["signingKeyId"]
        and manifest.get("checkpointSigningKeyId") == config["checkpointSigningKeyId"]
        and tip.get("checkpointSigningKeyId") == config["checkpointSigningKeyId"],
        "checkpoint-key-identity",
    )
    require(
        tip.get("checkpointCustodianCommitment")
        == inventory.commitment(
            config["commitmentKey"].read_bytes(),
            "custodian",
            config["checkpointCustodianId"],
        ),
        "checkpoint-custodian-commitment",
    )
    require(tip.get("capturedAt") == manifest["capturedAt"], "checkpoint-time-mismatch")
    require(
        all(
            tip.get(field) == manifest.get(field)
            for field in ("cycleId", "installationId", "lineageId", "epoch")
        ),
        "checkpoint-identity",
    )
    require(
        tip.get("sequence") == manifest["witnessCheckpoint"]["sequence"]
        and tip.get("hash") == manifest["witnessCheckpoint"]["hash"],
        "checkpoint-tip",
    )
    for name in (
        "leaseId",
        "captureNonce",
        "writerGeneration",
        "backupCaptureSequence",
        "backupCaptureHash",
        "maintenanceEvidenceSha256",
    ):
        require(tip.get(name) == manifest.get(name), "checkpoint-barrier-binding")
    # A signed older backup cannot become authoritative merely because its
    # own manifest verifies. Compare every retained independent checkpoint.
    for candidate in config["checkpointRoot"].glob("*.json"):
        candidate = private_path(candidate)
        verify_checkpoint_signature(config, candidate, candidate.with_suffix(".sig"))
        other = json.loads(candidate.read_bytes())
        require(
            other.get("format") == "claimcore-witness-checkpoint-1", "checkpoint-format"
        )
        require(
            other.get("installationId") == tip["installationId"]
            and other.get("lineageId") == tip["lineageId"],
            "checkpoint-identity",
        )
        require(other.get("epoch") <= tip["epoch"], "stale-backup-epoch")
        require(
            other.get("epoch") < tip["epoch"]
            or other.get("sequence") <= tip["sequence"],
            "stale-backup-tip",
        )
        require(
            other.get("epoch") != tip["epoch"]
            or other.get("sequence") != tip["sequence"]
            or other.get("hash") == tip["hash"],
            "checkpoint-divergence",
        )
    live_witness = metadata(config, "witness")
    require(
        live_witness[0] == tip["installationId"]
        and live_witness[1] == tip["lineageId"],
        "live-witness-identity",
    )
    require(
        int(live_witness[2]) == tip["epoch"]
        and int(live_witness[3]) == tip["sequence"]
        and live_witness[4] == tip["hash"],
        "stale-or-divergent-witness-backup",
    )
    attestations = {}
    for cluster in CLUSTERS:
        copy_id = manifest["copyIds"][cluster]
        uuid.UUID(copy_id)
        record_path = config["inventoryRoot"] / (
            cycle_id + "." + cluster + "." + copy_id + ".json"
        )
        verify_signature(config, record_path, record_path.with_suffix(".sig"))
        require(record_path.stat().st_size <= 64 * 1024, "attestation-size")
        record = json.loads(record_path.read_bytes())
        require(
            record_path.read_bytes() == inventory.canonical(record),
            "attestation-not-canonical",
        )
        require(
            record.get("format") == "claimcore-managed-copy-attestation-1"
            and record.get("copyId") == copy_id
            and record.get("cycleId") == cycle_id,
            "attestation-identity",
        )
        require(
            record.get("cluster") == cluster
            and record.get("kind") == "BASE"
            and record.get("state") == "UNVERIFIED"
            and record.get("primaryRegistration") == "NOT_REGISTERED",
            "attestation-state",
        )
        require(
            record.get("installationId") == tip["installationId"]
            and record.get("lineageId") == tip["lineageId"]
            and record.get("epoch") == tip["epoch"],
            "attestation-installation",
        )
        require(
            record.get("witnessCutoffSequence") == tip["sequence"]
            and record.get("witnessCutoffHash") == tip["hash"],
            "attestation-cutoff",
        )
        require(
            record.get("ciphertextSha256") == manifest[cluster]["ciphertextSha256"]
            and record.get("ciphertextBytes") == manifest[cluster]["ciphertextBytes"],
            "attestation-ciphertext",
        )
        base_fields = (
            "copyId",
            "eventId",
            "postgresSystemId",
            "timeline",
            "walSegmentBytes",
            "backupManifestSha256",
            "walStartLsn",
            "walEndLsn",
            "ciphertextSha256",
            "ciphertextBytes",
            "locationCommitment",
            "custodianCommitment",
        )
        base = manifest["baseCopies"][cluster]
        require(
            isinstance(base, dict)
            and set(base) == set(base_fields)
            and all(base[name] == record[name] for name in base_fields),
            "manifest-copy-evidence",
        )
        require(
            record.get("signingKeyId") == config["signingKeyId"]
            and record.get("encryptionKeyId") == config["encryptionKeyId"],
            "attestation-key-identity",
        )
        require(
            utc_timestamp(record.get("retainUntil")) > datetime.now(timezone.utc),
            "backup-retention-expired",
        )
        attestations[cluster] = record
    wal_count = verify_wal_inventory(config, attestations, tip)
    age = tool("age", "v1.3.2")
    pg_verify = tool("pg_verifybackup", "pg_verifybackup (PostgreSQL) 18.6")
    with tempfile.TemporaryDirectory(prefix="claimcore-restore-") as temporary:
        work = Path(temporary)
        work.chmod(0o700)
        for cluster in CLUSTERS:
            ciphertext = cycle / (cluster + ".tar.age")
            ciphertext = private_path(ciphertext)
            require(
                digest(ciphertext) == manifest[cluster]["ciphertextSha256"],
                "ciphertext-digest",
            )
            plain = work / (cluster + ".tar")
            with plain.open("xb") as output:
                decrypt = subprocess.Popen(
                    [
                        age,
                        "--decrypt",
                        "--identity",
                        str(private_path(config["ageIdentity"])),
                        str(ciphertext),
                    ],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                )
                total = 0
                while block := decrypt.stdout.read(1024 * 1024):
                    total += len(block)
                    if total > config["maxBackupBytes"]:
                        decrypt.kill()
                        decrypt.wait()
                        raise BackupFailure("backup-decryption-limit")
                    output.write(block)
                require(decrypt.wait() == 0, "decrypt-failed")
            data = work / cluster
            data.mkdir(mode=0o700)
            safe_extract(plain, data, config["maxBackupBytes"], config["maxTarEntries"])
            plain.unlink()
            pg_manifest_bytes = (data / "backup_manifest").read_bytes()
            pg_manifest = json.loads(pg_manifest_bytes)
            ranges = pg_manifest.get("WAL-Ranges")
            require(
                isinstance(ranges, list) and len(ranges) == 1, "backup-manifest-ranges"
            )
            record = attestations[cluster]
            require(
                hashlib.sha256(pg_manifest_bytes).hexdigest()
                == record["backupManifestSha256"],
                "backup-manifest-attestation",
            )
            require(
                str(pg_manifest.get("System-Identifier")) == record["postgresSystemId"],
                "backup-system-identity",
            )
            require(
                ranges[0].get("Timeline") == record["timeline"]
                and ranges[0].get("Start-LSN") == record["walStartLsn"]
                and ranges[0].get("End-LSN") == record["walEndLsn"],
                "backup-wal-range-attestation",
            )
            result = subprocess.run(
                [pg_verify, str(data)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
            require(result.returncode == 0, "pg-verifybackup-failed")
        report_path = work / "restore-report.json"
        result = subprocess.run(
            [
                verifier,
                str(work / "primary"),
                str(work / "witness"),
                str(source),
                str(checkpoint),
                str(report_path),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=False,
        )
        if result.returncode != 0:
            line = re.search(
                rb"synthetic restore verifier failed at line ([0-9]{1,3})",
                result.stderr,
            )
            reason = "test-restore-failed" + (
                "-line-" + line.group(1).decode("ascii") if line else ""
            )
            raise BackupFailure(reason)
        require(
            report_path.is_file() and not report_path.is_symlink(),
            "restore-report-missing",
        )
        report = json.loads(report_path.read_bytes())
        require(
            report.get("format") == "claimcore-restored-pair-report-1",
            "restore-report-format",
        )
        require(
            report.get("installationId") == manifest["installationId"]
            and report.get("lineageId") == manifest["lineageId"]
            and report.get("epoch") == manifest["epoch"],
            "restore-report-identity",
        )
        require(
            report.get("witnessCutoff") == tip["sequence"]
            and report.get("witnessHash") == tip["hash"],
            "restore-report-cutoff",
        )
        require(
            report.get("recoveredDataChecked") is True
            and report.get("pairCompared") is True,
            "restored-data-unchecked",
        )
        require(report.get("scope") == "synthetic-only", "restore-report-scope")
        print(
            json.dumps(
                {
                    "status": "functional-restore-verified",
                    "cycleId": cycle_id,
                    "qualificationScope": "synthetic-only",
                    "promotionAuthorized": False,
                    "locallyAttestedWalCopies": wal_count,
                    "walFreshnessQualified": False,
                    "managedErasureDeletionProved": False,
                }
            )
        )


def archive_wal(config, cluster, source, segment):
    require(cluster in CLUSTERS, "cluster-name")
    require(
        re.fullmatch(r"(?:[0-9A-F]{24}|[0-9A-F]{8}\.history)", segment) is not None,
        "wal-name",
    )
    require(Path(source).is_file() and not Path(source).is_symlink(), "wal-source")
    archive = config["archiveRoot"] / "wal" / cluster
    archive.mkdir(parents=True, mode=0o700, exist_ok=True)
    archive = private_path(archive, directory=True)
    target = archive / (segment + ".age")
    source_hash = digest(source)
    if target.exists():
        require(not target.is_symlink(), "wal-target-invalid")
        record = archive / (segment + ".json")
        require(
            record.is_file() and not record.is_symlink(), "wal-duplicate-needs-review"
        )
        existing_record = json.loads(record.read_bytes())
        require(
            existing_record.get("sourceSha256") == source_hash
            and existing_record.get("ciphertextSha256") == digest(target),
            "wal-duplicate-conflict",
        )
        sync_file(target)
        sync_file(record)
        sync_directory(archive)
        return
    temporary = archive / ("." + segment + "." + str(uuid.uuid4()))
    try:
        with temporary.open("xb") as sealed:
            result = subprocess.run(
                [
                    tool("age", "v1.3.2"),
                    "--encrypt",
                    "--recipient",
                    config["ageRecipient"],
                    str(source),
                ],
                stdout=sealed,
                stderr=subprocess.DEVNULL,
                check=False,
            )
            sealed.flush()
            os.fsync(sealed.fileno())
        require(result.returncode == 0, "wal-encryption-failed")
        require(digest(source) == source_hash, "wal-source-changed-during-archive")
        os.link(temporary, target, follow_symlinks=False)
    except FileExistsError:
        raise BackupFailure("wal-concurrent-duplicate-needs-review")
    finally:
        temporary.unlink(missing_ok=True)
    write_new(
        archive / (segment + ".json"),
        json_bytes(
            {
                "format": "claimcore-wal-copy-1",
                "cluster": cluster,
                "segment": segment,
                "sourceSha256": source_hash,
                "ciphertextSha256": digest(target),
                "archivedAt": inventory.utc_time(datetime.now(timezone.utc)),
            }
        ),
    )
    sync_directory(archive)


def main():
    os.umask(0o077)
    require(sys.version_info >= (3, 12), "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    sub = parser.add_subparsers(dest="command", required=True)
    verify = sub.add_parser("inspect")
    verify.add_argument("cycle_id")
    verify.add_argument("--restore-verifier", required=True)
    wal = sub.add_parser("archive-wal")
    wal.add_argument("cluster", choices=CLUSTERS)
    wal.add_argument("source")
    wal.add_argument("segment")
    attest = sub.add_parser("attest-wal")
    attest.add_argument("cluster", choices=CLUSTERS)
    attest.add_argument("segment")
    review = sub.add_parser("review-promotion")
    review.add_argument("--report", required=True)
    review.add_argument("--fence-report", required=True)
    review.add_argument("--approval", action="append", required=True)
    args = parser.parse_args()
    config = configuration(args.config, args.command == "archive-wal")
    if args.command == "inspect":
        inspect(config, args.cycle_id, args.restore_verifier)
    elif args.command == "attest-wal":
        attest_wal(config, args.cluster, args.segment)
    elif args.command == "review-promotion":
        require(
            "fenceVerificationKey" in config and "approverKeys" in config,
            "promotion-key-registry-missing",
        )
        require(
            isinstance(config["approverKeys"], dict)
            and len(config["approverKeys"]) >= 2,
            "promotion-approver-registry",
        )
        for key_id, entry in config["approverKeys"].items():
            uuid.UUID(key_id)
            uuid.UUID(entry["actorId"])
            entry["publicKey"] = private_path(entry["publicKey"])
        config["fenceVerificationKey"] = private_path(config["fenceVerificationKey"])
        result = promotion.review(
            config,
            args.report,
            args.fence_report,
            args.approval,
            private_path,
            tool("openssl"),
        )
        print(json.dumps(result))
    else:
        archive_wal(config, args.cluster, args.source, args.segment)


def failure_category(error):
    if isinstance(error, (BackupFailure, promotion.ReviewFailure)):
        candidate = error.args[0] if error.args else None
        if isinstance(candidate, str) and re.fullmatch(r"[a-z0-9-]{1,70}", candidate):
            return candidate
        return "backup-refusal"
    if isinstance(error, subprocess.TimeoutExpired):
        return "backup-subprocess-timeout"
    if isinstance(error, subprocess.CalledProcessError):
        return "backup-subprocess-failed"
    if isinstance(error, PermissionError):
        return "backup-permission-denied"
    if isinstance(error, FileNotFoundError):
        return "backup-file-unavailable"
    if isinstance(error, OSError):
        return "backup-os-error"
    if isinstance(error, (json.JSONDecodeError, KeyError, TypeError, ValueError)):
        return "backup-data-invalid"
    return "unexpected-backup-failure"


def _code_line(traceback):
    line = None
    while traceback is not None:
        if traceback.tb_frame.f_code.co_filename == __file__:
            line = traceback.tb_lineno
        traceback = traceback.tb_next
    return line if isinstance(line, int) and 1 <= line <= 9999 else None


def report_failure(_exception_type, error, traceback):
    category = failure_category(error)
    if category == "backup-permission-denied":
        line = _code_line(traceback)
        if line is not None:
            category += f"-line-{line}"
    print(json.dumps({"status": "quarantined", "reason": category}), file=sys.stderr)


if __name__ == "__main__":
    sys.excepthook = report_failure
    main()
