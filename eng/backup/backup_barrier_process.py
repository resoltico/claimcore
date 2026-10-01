"""Fixed packaged Database capture owner over one inherited private duplex FD."""

import os
import socket
import subprocess
import sys
from types import TracebackType
from typing import Self

from deployment_common import DeploymentRefusalError, private_path, require
from deployment_publication import package_binary

FRAME_LIMIT = 16384
RECEIVE_BLOCK = 4096
SOCKET_TIMEOUT_SECONDS = 30
SHUTDOWN_SECONDS = 2


class DatabaseBarrierController:
    """Runs the packaged owner process and speaks framed requests to it."""

    def __init__(
        self, archive_root: str | os.PathLike[str], checkpoint_root: str | os.PathLike[str]
    ) -> None:
        """Start the packaged owner process on a private socket pair."""
        require(sys.platform in ("darwin", "linux"), "barrier-platform")
        binary = package_binary()
        require(binary.is_file(), "barrier-owner-unavailable")
        archive = private_path(archive_root, directory=True)
        checkpoint = private_path(checkpoint_root, directory=True)
        parent, child = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
        parent.settimeout(SOCKET_TIMEOUT_SECONDS)
        environment = os.environ.copy()
        environment["CLAIMCORE_BACKUP_CONTROL_FD"] = str(child.fileno())
        environment["CLAIMCORE_BACKUP_ARCHIVE_ROOT"] = str(archive)
        environment["CLAIMCORE_BACKUP_CHECKPOINT_ROOT"] = str(checkpoint)
        try:
            process = subprocess.Popen(
                ["dotnet", str(binary), "hold-backup-capture"],
                pass_fds=(child.fileno(),),
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
            )
        except OSError:
            parent.close()
            child.close()
            msg = "barrier-owner-unavailable"
            raise DeploymentRefusalError(msg) from None
        child.close()
        self.socket = parent
        self.process = process

    def exchange(self, raw: bytes) -> bytes:
        """Send one frame and return the owner's newline-terminated answer."""
        require(
            isinstance(raw, bytes) and 1 < len(raw) <= FRAME_LIMIT and raw.endswith(b"\n"),
            "barrier-frame-limit",
        )
        try:
            self.socket.sendall(raw)
            received = bytearray()
            while len(received) < FRAME_LIMIT:
                block = self.socket.recv(min(RECEIVE_BLOCK, FRAME_LIMIT - len(received)))
                if not block:
                    msg = "barrier-owner-unavailable"
                    raise DeploymentRefusalError(msg)
                received.extend(block)
                if received.endswith(b"\n"):
                    return bytes(received)
            msg = "barrier-frame-limit"
            raise DeploymentRefusalError(msg)
        except (OSError, TimeoutError):
            msg = "barrier-owner-unavailable"
            raise DeploymentRefusalError(msg) from None

    def observe(self, raw: bytes) -> bytes:
        """Read the durable receipt back through the same fixed session."""
        # The fixed Database session performs a separate durable receipt readback.
        return self.exchange(raw)

    def close(self) -> None:
        """Close the session and stop the owner process."""
        self.socket.close()
        try:
            self.process.wait(timeout=SHUTDOWN_SECONDS)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()

    def __enter__(self) -> Self:
        """Return the running controller."""
        return self

    def __exit__(
        self,
        _type: type[BaseException] | None,
        _value: BaseException | None,
        _traceback: TracebackType | None,
    ) -> None:
        """Stop the owner process."""
        self.close()
