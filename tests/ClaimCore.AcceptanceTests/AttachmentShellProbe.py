"""Check already accepted and fresh withdrawals through the published terminal shell."""

import os
import shlex
import sys

from ShellTerminalProbe import Terminal


def run(cli, reference, case_id, attachment, accepted, fresh):
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
        terminal.send(b"exit\n")
        terminal.finish(0)
    finally:
        terminal.close()


if __name__ == "__main__":
    run(*sys.argv[1:])
