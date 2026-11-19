"""Check attachment refusals and withdrawal retries in the published terminal shell."""

import os
import shlex
import sys

from ShellTerminalProbe import Terminal


def run(cli, reference, case_id, attachment, accepted, fresh, blocked_path, blocked_id):
    terminal = Terminal(cli, os.environ)

    def command(arguments, expected):
        start = terminal.offset
        terminal.command(shlex.join(arguments).encode(), expected)
        rendered = terminal.output[start:terminal.offset].lower()
        assert b"not recorded" not in rendered, "The shell did not obtain authoritative preview"
        return rendered

    try:
        terminal.expect(b"claimcore> ")
        command(["case", "attachments", reference], 0)
        command(
            ["attachment", "withdraw", reference, attachment, "--operation-id", accepted],
            0,
        )
        command(
            ["attachment", "withdraw", "--case-id", case_id, attachment,
             "--operation-id", accepted],
            0,
        )
        command(
            ["attachment", "withdraw", reference, attachment, "--operation-id", fresh],
            2,
        )
        start = terminal.offset
        terminal.command(shlex.join([
            "attachment", "add", reference, "--file", blocked_path, "--operation-id", blocked_id,
        ]).encode(), 2)
        rendered = terminal.output[start:terminal.offset].lower()
        assert b"files of this type can run programs" in rendered, "Expected Core type refusal"
        terminal.send(b"exit\n")
        terminal.finish(0)
    finally:
        terminal.close()


if __name__ == "__main__":
    run(*sys.argv[1:])
