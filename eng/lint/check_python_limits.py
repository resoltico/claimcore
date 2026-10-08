"""Fail when a Python function is longer than the repository allows.

Ruff limits complexity and statement counts but not physical length, and the repository limits every
function to 50 lines in every language. The length counts every line from the `def` line (or its
first decorator) to its last line, docstrings and blank lines included, exactly as the F# and
TypeScript gates count.
"""

import ast
import sys
from pathlib import Path

MAX_FUNCTION_LINES = 50
ROOTS = ("eng/backup", "eng/lint")


def _first_line(node: ast.FunctionDef | ast.AsyncFunctionDef) -> int:
    return min([node.lineno, *(decorator.lineno for decorator in node.decorator_list)])


def oversized(path: Path) -> list[str]:
    """Return one message for each function in `path` that exceeds the limit."""
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    findings = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.FunctionDef | ast.AsyncFunctionDef):
            continue
        length = (node.end_lineno or node.lineno) - _first_line(node) + 1
        if length > MAX_FUNCTION_LINES:
            findings.append(
                f"{path}:{node.lineno} {node.name} has {length} lines; "
                f"the maximum is {MAX_FUNCTION_LINES}. Split the responsibility."
            )
    return findings


def main(argv: list[str]) -> int:
    """Check every Python file under the roots, or the files named on the command line."""
    files = [Path(name) for name in argv] or sorted(
        path for root in ROOTS for path in Path(root).rglob("*.py")
    )
    findings = [message for path in files for message in oversized(path)]
    for message in findings:
        sys.stderr.write(message + "\n")
    if findings:
        sys.stderr.write(f"{len(findings)} oversized Python function(s).\n")
        return 1
    sys.stdout.write(f"Python function lengths are within {MAX_FUNCTION_LINES} lines.\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
