"""Real human settlement consent against the owned published HTTPS fixture."""

import json
import os
import sys
import time
from ShellTerminalProbe import Terminal


def wait(terminal, literal):
    deadline = time.monotonic() + 75
    while time.monotonic() < deadline:
        found = terminal.output.find(literal, terminal.offset)
        if found >= 0:
            terminal.offset = found + len(literal)
            return
        if terminal.child.poll() is not None:
            raise RuntimeError("Settlement shell ended before authenticated consent")
        terminal.read(0.05)
    raise RuntimeError("Authenticated settlement prompt deadline")


def run(cli, operation, actor, installation, answer):
    terminal = Terminal(cli, os.environ.copy())
    stage = "shell-prompt"
    try:
        terminal.expect(b"claimcore> ")
        terminal.send(f"operation settle {operation}\n".encode())
        stage = "authenticated-scope"
        wait(terminal, f"Account {actor} in installation {installation}.".encode())
        stage = "operation-identity"
        wait(terminal, f"Operation ID: {operation}".encode())
        stage = "permanent-effect"
        wait(terminal, b"permanently block its operation ID")
        stage = "confirmation-question"
        wait(terminal, b"Record these changes? [y/N] ")
        stage = "confirmation-result"
        terminal.send((answer + "\n").encode())
        terminal.expect(f"Exit code: {2 if answer == 'yes' else 130}".encode())
        terminal.expect(b"claimcore> ")
        terminal.send(b"exit\n")
        terminal.finish(0)
    except Exception:
        print(json.dumps({"settlementStage": stage, "outputBytes": len(terminal.output),
                          "childExited": terminal.child.poll() is not None}), file=sys.stderr)
        raise
    finally:
        target = os.environ.get("SETTLEMENT_PROBE_PRIVATE_OUTPUT")
        if target:
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(descriptor, "wb") as output:
                output.write(terminal.output)
        terminal.close()
    print(json.dumps({"authenticatedScope": True, "exactOperation": True,
                      "explicitConsent": answer == "yes", "shellContinued": True}))


if __name__ == "__main__":
    run(*sys.argv[1:])
