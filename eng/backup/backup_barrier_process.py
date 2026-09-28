"""Fixed packaged Database capture owner over one inherited private duplex FD."""

import os
import socket
import subprocess
import sys

from deployment_common import DeploymentRefusal, private_path, require
from deployment_publication import package_binary


class DatabaseBarrierController:
    def __init__(self, archive_root, checkpoint_root):
        require(sys.platform in ("darwin", "linux"), "barrier-platform")
        binary = package_binary()
        require(binary.is_file(), "barrier-owner-unavailable")
        archive = private_path(archive_root, directory=True)
        checkpoint = private_path(checkpoint_root, directory=True)
        parent, child = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
        parent.settimeout(30)
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
            raise DeploymentRefusal("barrier-owner-unavailable") from None
        child.close()
        self.socket = parent
        self.process = process

    def exchange(self, raw):
        require(
            isinstance(raw, bytes) and 1 < len(raw) <= 16384 and raw.endswith(b"\n"),
            "barrier-frame-limit",
        )
        try:
            self.socket.sendall(raw)
            received = bytearray()
            while len(received) < 16384:
                block = self.socket.recv(min(4096, 16384 - len(received)))
                if not block:
                    raise DeploymentRefusal("barrier-owner-unavailable")
                received.extend(block)
                if received.endswith(b"\n"):
                    return bytes(received)
            raise DeploymentRefusal("barrier-frame-limit")
        except (OSError, TimeoutError):
            raise DeploymentRefusal("barrier-owner-unavailable") from None

    def observe(self, raw):
        # The fixed Database session performs a separate durable receipt readback.
        return self.exchange(raw)

    def close(self):
        self.socket.close()
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()

    def __enter__(self):
        return self

    def __exit__(self, _type, _value, _traceback):
        self.close()
