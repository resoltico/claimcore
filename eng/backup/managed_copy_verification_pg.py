"""Isolated PostgreSQL BASE/WAL inspection; never an installation-readiness claim."""

import hashlib
import json
import os
import re
import subprocess
import tarfile
from pathlib import Path

from managed_copy_verification_io import VerificationFailure, require, tool

ROOT = Path(__file__).resolve().parents[2]
PGDATA = "/var/lib/postgresql/18/docker"


def _run(args, timeout=120):
    result = subprocess.run(
        args,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=timeout,
        check=False,
    )
    name = Path(args[0]).name
    require(
        result.returncode == 0,
        "POSTGRES_" + name.upper().replace("-", "_") + "_REFUSED",
    )
    return result.stdout.decode("ascii", "strict").strip()


def decrypt(config, destination):
    age = tool("age", "v1.3.2")
    descriptor = os.open(
        config["ciphertextFile"], os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    )
    try:
        child = subprocess.Popen(
            [age, "--decrypt", "--identity", config["ageIdentityFile"]],
            stdin=descriptor,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
    finally:
        os.close(descriptor)
    digest = hashlib.sha256()
    limit = config["maximumPlaintextBytes"]
    total = 0
    with destination.open("xb") as output:
        while True:
            block = child.stdout.read(65536)
            if not block:
                break
            total += len(block)
            if total > limit:
                child.kill()
                child.wait()
                raise VerificationFailure("DECRYPTION_LIMIT")
            digest.update(block)
            output.write(block)
        output.flush()
        os.fsync(output.fileno())
    require(child.wait(timeout=120) == 0 and total > 0, "DECRYPTION_REFUSED")
    return total, digest.hexdigest()


def extract_base(source, destination, maximum, maximum_entries):
    with tarfile.open(source, "r:") as archive:
        entries = archive.getmembers()
        require(0 < len(entries) <= maximum_entries, "BACKUP_ENTRY_LIMIT")
        names = set()
        total = 0
        for item in entries:
            name = Path(item.name)
            normalized = name.as_posix().removeprefix("./")
            require(
                not name.is_absolute()
                and ".." not in name.parts
                and (item.isfile() or item.isdir())
                and normalized not in names,
                "UNSAFE_BACKUP_ENTRY",
            )
            names.add(normalized)
            total += item.size
            require(
                0 <= item.size <= maximum and total <= maximum, "BACKUP_EXPANSION_LIMIT"
            )
    _run([tool("tar", None), "-C", str(destination), "-xf", str(source)], 120)
    observed = set()
    extracted_bytes = 0
    for base, directories, files in os.walk(destination, followlinks=False):
        for name in directories + files:
            path = Path(base) / name
            relative = path.relative_to(destination).as_posix()
            info = path.lstat()
            require(
                relative in names
                and not path.is_symlink()
                and (path.is_dir() or path.is_file())
                and (not path.is_file() or info.st_nlink == 1),
                "EXTRACTED_BACKUP_DIVERGED",
            )
            observed.add(relative)
            if path.is_file():
                extracted_bytes += info.st_size
                require(extracted_bytes <= maximum, "BACKUP_EXPANSION_LIMIT")
    require(len(observed) <= maximum_entries, "BACKUP_ENTRY_LIMIT")


def _image():
    with (ROOT / "db/postgresql-baseline.json").open("rb") as stream:
        image = json.load(stream)["containerImage"]
    require(isinstance(image, str) and "@sha256:" in image, "POSTGRES_IMAGE_UNPINNED")
    return image


def _docker(args, timeout=120):
    try:
        return _run([tool("docker", None), *args], timeout)
    except VerificationFailure:
        operation = next(
            (
                piece
                for piece in args
                if piece in ("mkdir", "cp", "chown", "chmod", "pg_ctl", "psql")
            ),
            args[0],
        )
        raise VerificationFailure("DOCKER_" + operation.upper() + "_REFUSED") from None


def boot_identity(config, data):
    image = _image()
    identifier = _docker(
        [
            "run",
            "--rm",
            "-d",
            "--network",
            "none",
            "--label",
            "org.claimcore.copy-verification=synthetic-owner",
            "--entrypoint",
            "sleep",
            image,
            "900",
        ]
    )
    require(
        re.fullmatch(r"[0-9a-f]{64}", identifier) is not None,
        "ISOLATED_CONTAINER_REFUSED",
    )
    try:
        _docker(["exec", "-u", "root", identifier, "mkdir", "-p", PGDATA])
        _docker(["cp", str(data) + "/.", identifier + ":" + PGDATA], 180)
        _docker(
            [
                "exec",
                "-u",
                "root",
                identifier,
                "chown",
                "-R",
                "postgres:postgres",
                PGDATA,
            ],
            180,
        )
        _docker(["exec", "-u", "root", identifier, "chmod", "700", PGDATA])
        _docker(
            [
                "exec",
                "-u",
                "postgres",
                identifier,
                "pg_ctl",
                "-D",
                PGDATA,
                "-l",
                "/tmp/copy-verification.log",
                "start",
            ],
            120,
        )
        role = config["databaseOwnerRole"]
        database = config["databaseName"]
        system = _docker(
            [
                "exec",
                "-u",
                "postgres",
                identifier,
                "psql",
                "-X",
                "-A",
                "-t",
                "-h",
                "/var/run/postgresql",
                "-p",
                "5432",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                role,
                "-d",
                database,
                "-c",
                "SELECT system_identifier::text FROM pg_control_system()",
            ]
        )
        require(system == config["postgresSystemId"], "RECOVERED_SYSTEM_DIVERGED")
        query = (
            "SELECT installation_id::text || ':' || lineage_id::text || ':' || "
            "witness_epoch::text FROM claimcore.installation_lineage WHERE singleton"
            if config["cluster"] == "PRIMARY"
            else "SELECT installation_id::text || ':' || lineage_id::text || ':' || "
            "epoch::text FROM claimcore_witness.installation WHERE singleton"
        )
        values = _docker(
            [
                "exec",
                "-u",
                "postgres",
                identifier,
                "psql",
                "-X",
                "-A",
                "-t",
                "-h",
                "/var/run/postgresql",
                "-p",
                "5432",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                role,
                "-d",
                database,
                "-c",
                query,
            ]
        )
        recovered = values.split(":")
        require(len(recovered) == 3, "RECOVERED_IDENTITY_DIVERGED")
        require(
            re.fullmatch(
                r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-"
                r"[0-9a-f]{4}-[0-9a-f]{12}",
                recovered[0],
            )
            is not None,
            "RECOVERED_INSTALLATION_FORMAT_DIVERGED",
        )
        require(
            recovered[0] == config["installationId"], "RECOVERED_INSTALLATION_DIVERGED"
        )
        require(recovered[1] == config["lineageId"], "RECOVERED_LINEAGE_DIVERGED")
        require(recovered[2] == str(config["witnessEpoch"]), "RECOVERED_EPOCH_DIVERGED")
        timeline = _docker(
            [
                "exec",
                "-u",
                "postgres",
                identifier,
                "psql",
                "-X",
                "-A",
                "-t",
                "-h",
                "/var/run/postgresql",
                "-p",
                "5432",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                role,
                "-d",
                database,
                "-c",
                "SELECT timeline_id::bigint FROM pg_control_checkpoint()",
            ]
        )
        count_query = (
            "SELECT count(*) FROM claimcore.case_changes"
            if config["cluster"] == "PRIMARY"
            else "SELECT count(*) FROM claimcore_witness.journal"
        )
        count = _docker(
            [
                "exec",
                "-u",
                "postgres",
                identifier,
                "psql",
                "-X",
                "-A",
                "-t",
                "-h",
                "/var/run/postgresql",
                "-p",
                "5432",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                role,
                "-d",
                database,
                "-c",
                count_query,
            ]
        )
        require(
            system == config["postgresSystemId"]
            and int(timeline) == config["timeline"]
            and count.isascii()
            and count.isdigit(),
            "RECOVERED_DATA_DIVERGED",
        )
        return int(count)
    finally:
        subprocess.run(
            [tool("docker", None), "stop", identifier],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=30,
            check=False,
        )


def verify_base(config, decrypted, work):
    data = work / "data"
    data.mkdir(mode=0o700)
    extract_base(
        decrypted, data, config["maximumPlaintextBytes"], config["maximumTarEntries"]
    )
    manifest_file = data / "backup_manifest"
    require(
        manifest_file.is_file() and manifest_file.stat().st_size <= 32 * 1024 * 1024,
        "BACKUP_MANIFEST_MISSING",
    )
    manifest_bytes = manifest_file.read_bytes()
    manifest = json.loads(manifest_bytes)
    ranges = manifest.get("WAL-Ranges")
    require(
        manifest.get("PostgreSQL-Backup-Manifest-Version") == 2
        and str(manifest.get("System-Identifier")) == config["postgresSystemId"]
        and isinstance(ranges, list)
        and len(ranges) == 1,
        "BACKUP_MANIFEST_DIVERGED",
    )
    wal = ranges[0]
    require(
        wal.get("Timeline") == config["timeline"]
        and wal.get("Start-LSN") == config["walStartLsn"]
        and wal.get("End-LSN") == config["walEndLsn"]
        and hashlib.sha256(manifest_bytes).hexdigest()
        == config["backupManifestSha256"],
        "BACKUP_WAL_RANGE_DIVERGED",
    )
    _run([tool("pg_verifybackup", "pg_verifybackup (PostgreSQL) 18.6"), str(data)], 180)
    count = boot_identity(config, data)
    return {
        "pgVerifyBackup": True,
        "isolatedBoot": True,
        "recoveredIdentityChecked": True,
        "recoveredRowCount": count,
        "pgVerifyBackupManifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
        "walFirstRecordParsed": False,
        "walTimelineMatched": False,
    }


def verify_wal(config, decrypted):
    name = config["walSegment"]
    size = config["walSegmentBytes"]
    require(
        re.fullmatch(r"[0-9A-F]{24}", name) is not None
        and int(name[:8], 16) == config["timeline"]
        and decrypted.stat().st_size == size,
        "WAL_SEGMENT_DIVERGED",
    )
    renamed = decrypted.with_name(name)
    decrypted.rename(renamed)
    _run(
        [
            tool("pg_waldump", "pg_waldump (PostgreSQL) 18.6"),
            "--quiet",
            "--limit=1",
            str(renamed),
        ],
        60,
    )
    return {
        "pgVerifyBackup": False,
        "isolatedBoot": False,
        "recoveredIdentityChecked": False,
        "recoveredRowCount": None,
        "pgVerifyBackupManifestSha256": None,
        "walFirstRecordParsed": True,
        "walTimelineMatched": True,
    }
