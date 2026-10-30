"""Bounded Unix terminal checks for the published CLI; no browser or network is needed."""

import errno
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import signal
import subprocess
import sys
import termios
import time


class Terminal:
    def __init__(self, cli, environment, redirect_output=False):
        self.master, slave = pty.openpty()
        self.output = bytearray()
        self.offset = 0

        def session():
            os.setsid()
            fcntl.ioctl(slave, termios.TIOCSCTTY, 0)

        try:
            self.child = subprocess.Popen(
                ["dotnet", cli, "shell"], stdin=slave, stdout=subprocess.PIPE if redirect_output else slave, stderr=slave,
                env=environment, preexec_fn=session, close_fds=True,
            )
        except BaseException:
            os.close(self.master)
            self.master = None
            raise
        finally:
            os.close(slave)

    def read(self, timeout):
        readers = [self.master]
        if self.child.stdout is not None:
            readers.append(self.child.stdout.fileno())
        for descriptor in select.select(readers, [], [], timeout)[0]:
            try:
                chunk = os.read(descriptor, 4096)
            except OSError as error:
                if error.errno != errno.EIO:
                    raise
                chunk = b""
            self.output.extend(chunk)
        if len(self.output) > 1048576:
            raise RuntimeError("Terminal output exceeded its bound")

    def expect(self, literal):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            found = self.output.find(literal, self.offset)
            if found >= 0:
                self.offset = found + len(literal)
                return
            if self.child.poll() is not None:
                raise RuntimeError("Shell exited before the expected terminal state")
            self.read(0.05)
        raise RuntimeError("Shell terminal state deadline")

    def send(self, source):
        os.write(self.master, source)

    def command(self, source, code):
        self.send(source + b"\n")
        self.expect(f"Exit code: {code}".encode())
        self.expect(b"claimcore> ")

    def finish(self, expected):
        try:
            code = self.child.wait(timeout=8)
            if code != expected:
                raise RuntimeError("Unexpected shell process exit")
        finally:
            self.close()

    def close(self):
        if self.child.poll() is None:
            os.killpg(self.child.pid, signal.SIGKILL)
            self.child.wait(timeout=3)
        if self.master is not None:
            os.close(self.master)
            self.master = None
        if self.child.stdout is not None:
            self.child.stdout.close()


def run(cli, home):
    environment = {
        key: value for key, value in os.environ.items()
        if not key.upper().startswith("CLAIMCORE_")
    }
    environment.pop("XDG_CONFIG_HOME", None)
    environment.update({
        "HOME": home, "TERM": "xterm-256color", "DOTNET_NOLOGO": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "PYTHONDONTWRITEBYTECODE": "1",
        "CLAIMCORE_SERVICE_URL": "https://claims.example.test/",
        "CLAIMCORE_OIDC_ISSUER": "https://identity.example.test/realm",
        "CLAIMCORE_OIDC_CLIENT_ID": "synthetic-cli",
        "CLAIMCORE_CLI_AUTH_MODE": "interactive",
    })
    if list(Path(home).iterdir()):
        raise RuntimeError("HOME must start empty")

    terminal = Terminal(cli, environment)
    try:
        terminal.expect(b"claimcore> ")
        terminal.command(b"help 'overview'", 0)
        terminal.command(b"help\toverview", 0)
        terminal.command(b"\x1b[A", 0)
        terminal.command(b"help 'unterminated", 64)
        terminal.command(b"help 'overview; touch marker'", 64)
        terminal.send(b"help unfinished")
        terminal.expect(b"help unfinished")
        os.kill(terminal.child.pid, signal.SIGINT)
        terminal.expect(b"claimcore> ")
        os.kill(terminal.child.pid, signal.SIGINT)
        terminal.finish(130)
    finally:
        terminal.close()

    for ending, expected in [(b"exit\n", 0), (b"\x04", 0), (None, 130)]:
        terminal = Terminal(cli, environment)
        try:
            terminal.expect(b"claimcore> ")
            if ending is None:
                os.kill(terminal.child.pid, signal.SIGTERM)
            else:
                terminal.send(ending)
            terminal.finish(expected)
        except subprocess.TimeoutExpired as error:
            raise RuntimeError(f"Shell did not exit for ending {ending!r}") from error
        finally:
            terminal.close()

    terminal = Terminal(cli, environment, redirect_output=True)
    try:
        terminal.expect(b"claimcore> ")
        terminal.command(b"case close REF", 64)
        terminal.send(b"exit\n")
        terminal.finish(0)
    finally:
        terminal.close()

    environment["CLAIMCORE_CLI_AUTH_MODE"] = "automation"
    environment["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] = str(Path(home) / "not-created")
    terminal = Terminal(cli, environment)
    try:
        terminal.finish(3)
    finally:
        terminal.close()

    if list(Path(home).iterdir()):
        raise RuntimeError("Shell wrote files under HOME")
    print(json.dumps({"history": True, "literalMetacharacters": True, "quoting": True,
                      "interrupt": True, "exit": True, "eof": True, "sigterm": True,
                      "automationRefused": True, "redirectedReviewRefused": True, "homeUnchanged": True}))


if __name__ == "__main__":
    run(sys.argv[1], sys.argv[2])
