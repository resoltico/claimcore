#!/usr/bin/env python3
"""Reject FSharpLint directives without an immediately preceding reason comment."""
import pathlib
import re
import sys


def violations(source):
    lines = source.splitlines()
    result = []
    for index, line in enumerate(lines):
        if re.search(r"^\s*//\s*fsharplint:disable", line, re.IGNORECASE):
            previous = lines[index - 1].strip() if index else ""
            explanation = previous[2:].strip() if previous.startswith("//") else ""
            if not explanation or re.match(r"fsharplint:", explanation, re.IGNORECASE):
                result.append(index + 1)
    return result


def check(paths):
    failed = False
    for path in paths:
        for line in violations(path.read_text(encoding="utf-8")):
            print(f"{path}:{line}: FSharpLint suppression needs a preceding reason comment", file=sys.stderr)
            failed = True
    return int(failed)


def sources(roots):
    for root in roots:
        for path in sorted(root.rglob("*")):
            if path.suffix in {".fs", ".fsi", ".fsx"}:
                yield path


def controls():
    assert violations("// fsharplint:disable-next-line Rule\nlet x = 1") == [1]
    assert violations("// Another directive is not a reason.\n// fsharplint:disable Rule") == []
    assert violations("// fsharplint:disable Rule\n// fsharplint:disable Other") == [1, 2]
    assert violations("// \n// fsharplint:disable Rule") == [2]
    assert violations("// Exhaustive domain match needs all cases.\n// fsharplint:disable-next-line Rule") == []


if __name__ == "__main__":
    if sys.argv[1:] == ["--controls"]:
        controls()
    else:
        raise SystemExit(check(sources(map(pathlib.Path, sys.argv[1:] or ["src", "tests", "eng"]))))
