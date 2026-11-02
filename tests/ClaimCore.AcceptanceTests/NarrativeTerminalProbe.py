"""Real published CLI/PTY display boundary for synthetic retained plaintext values.

The HTTPS fixture changes only selected read responses after actual authorization;
this probe does not claim that current admission accepted the unsafe text.
"""
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
            raise RuntimeError("Published shell exited before narrative output")
        terminal.read(0.05)
    raise RuntimeError("Published narrative terminal deadline")


def segment(output, start, end):
    begin = output.index(start)
    finish = output.index(end, begin) + len(end)
    return bytes(output[begin:finish]).replace(b"\r\n", b"\n")


def run(cli, reference, reminder):
    terminal = Terminal(cli, os.environ.copy())
    try:
        terminal.expect(b"claimcore> ")
        terminal.send(f"case get {reference} --format text\n".encode())
        wait(terminal, b"END-DESCRIPTION")
        wait(terminal, b"Exit code: 0")
        terminal.expect(b"claimcore> ")
        terminal.send(f"reminder get {reference} --reminder-id {reminder} --format text\n".encode())
        wait(terminal, b"END-ACTION")
        wait(terminal, b"Exit code: 0")
        terminal.expect(b"claimcore> ")
        terminal.send(b"exit\n")
        terminal.finish(0)
        suffix = "\\u{001B}[31m\\u{0085}\\u{202E}العربية\u200c\u200d-literal\\\\u{001B} "
        for name in ["DESCRIPTION", "SUMMARY"]:
            actual = segment(terminal.output, f"RETAINED-{name}-FIRST".encode(), f"END-{name}".encode())
            expected = (f"RETAINED-{name}-FIRST\n\nRETAINED-{name}-SECOND" + suffix + f"END-{name}").encode()
            if actual != expected:
                raise RuntimeError(f"Retained {name.lower()} paragraph/escape output differs")
            if any(control in actual for control in [b"\x1b", b"\xc2\x85", b"\xe2\x80\xae"]):
                raise RuntimeError("Retained terminal control was emitted")
        action = segment(terminal.output, b"RETAINED-ACTION", b"END-ACTION")
        if action != ("RETAINED-ACTION" + suffix + "END-ACTION").encode():
            raise RuntimeError("Retained reminder action escape output differs")
    finally:
        target = os.environ.get("NARRATIVE_PROBE_PRIVATE_OUTPUT")
        if target:
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(descriptor, "wb") as output:
                output.write(terminal.output)
        terminal.close()
    print(json.dumps({"realPty": True, "lfParagraphs": True, "retainedControlsInert": True,
                      "arabicJoinersExact": True, "literalEscapeDistinguished": True}))


if __name__ == "__main__":
    run(*sys.argv[1:])
