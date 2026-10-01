"""Decrypt and safely extract one managed copy for isolated verification."""

import hashlib
import os
import subprocess
import tarfile
from pathlib import Path

from backup_types import JsonObject
from managed_copy_verification_io import NO_FOLLOW, VerificationFailureError, require, tool

READ_BLOCK = 65536
COMMAND_TIMEOUT_SECONDS = 120


def run_fixed(args: list[str], timeout: int = COMMAND_TIMEOUT_SECONDS) -> str:
    """Run a fixed argv and return its trimmed stdout, refusing with a tool-specific code."""
    result = subprocess.run(
        args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=timeout, check=False
    )
    name = Path(args[0]).name
    require(result.returncode == 0, "POSTGRES_" + name.upper().replace("-", "_") + "_REFUSED")
    return result.stdout.decode("ascii", "strict").strip()


def decrypt(config: JsonObject, destination: Path) -> tuple[int, str]:
    """Decrypt the ciphertext copy to `destination`, returning its length and SHA-256."""
    age = tool("age", "v1.3.2")
    descriptor = os.open(config["ciphertextFile"], os.O_RDONLY | NO_FOLLOW)
    try:
        child = subprocess.Popen(
            [age, "--decrypt", "--identity", config["ageIdentityFile"]],
            stdin=descriptor,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
    finally:
        os.close(descriptor)
    stream = child.stdout
    if stream is None:
        msg = "DECRYPTION_REFUSED"
        raise VerificationFailureError(msg)
    digest = hashlib.sha256()
    limit = config["maximumPlaintextBytes"]
    total = 0
    with destination.open("xb") as output:
        while block := stream.read(READ_BLOCK):
            total += len(block)
            if total > limit:
                child.kill()
                child.wait()
                msg = "DECRYPTION_LIMIT"
                raise VerificationFailureError(msg)
            digest.update(block)
            output.write(block)
        output.flush()
        os.fsync(output.fileno())
    require(child.wait(timeout=COMMAND_TIMEOUT_SECONDS) == 0 and total > 0, "DECRYPTION_REFUSED")
    return total, digest.hexdigest()


def _planned_entries(source: Path, maximum: int, maximum_entries: int) -> set[str]:
    with tarfile.open(source, "r:") as archive:
        entries = archive.getmembers()
        require(0 < len(entries) <= maximum_entries, "BACKUP_ENTRY_LIMIT")
        names: set[str] = set()
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
            require(0 <= item.size <= maximum and total <= maximum, "BACKUP_EXPANSION_LIMIT")
    return names


def extract_base(source: Path, destination: Path, maximum: int, maximum_entries: int) -> None:
    """Extract a base backup only if every entry is planned, safe and stays within bounds."""
    names = _planned_entries(source, maximum, maximum_entries)
    run_fixed(
        [tool("tar", None), "-C", str(destination), "-xf", str(source)], COMMAND_TIMEOUT_SECONDS
    )
    observed: set[str] = set()
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
