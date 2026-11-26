"""Require completion of every selected native test in the runner's TRX report."""

import sys
from pathlib import Path
from xml.etree import ElementTree


def require_completed(report):
    counters = ElementTree.parse(report).getroot().findall("{*}ResultSummary/{*}Counters")
    if len(counters) != 1:
        raise ValueError("Expected one TRX result summary.")
    total, executed, passed = (int(counters[0].attrib[key]) for key in ("total", "executed", "passed"))
    if not 0 < total == executed == passed:
        raise ValueError(f"Selected native tests were incomplete: {total} total, {executed} executed, {passed} passed.")


if __name__ == "__main__":
    report = Path(sys.argv[1])
    try:
        require_completed(report)
    except (OSError, ElementTree.ParseError, KeyError, ValueError) as error:
        sys.exit(f"{report}: {error}")
