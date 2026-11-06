#!/usr/bin/env python3
"""Partition discovered acceptance cases and check the actual executed subset."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET


BROWSER = "published browser qualification."


def partition(names):
    if len(names) != len(set(names)):
        raise ValueError("Acceptance test names must be unique")
    browser = sorted(name for name in names if name.startswith(BROWSER))
    native = sorted(set(names) - set(browser))
    if len(browser) != 2 or not native:
        raise ValueError("Expected browser completion control and engine execution, plus native cases")
    return {"native": native, "browser": browser}


def discovered(output):
    # MTP still emits SGR around its summary under GITHUB_ACTIONS, even with NO_COLOR.
    plain = re.sub(r"\x1b\[[0-9;]*m", "", output)
    names = re.findall(r"^  (\S.*)$", plain, re.M)
    counts = re.findall(r"^Discovered ([0-9]+) tests\.$", plain, re.M)
    if len(counts) != 1 or len(names) != int(counts[0]):
        raise ValueError("Incomplete acceptance discovery output")
    return names


def completed(expected, document):
    results = document.findall(".//{*}UnitTestResult")
    actual = sorted(result.attrib["testName"] for result in results)
    if actual != expected or any(result.attrib["outcome"] != "Passed" for result in results):
        raise ValueError("Acceptance scope must execute every selected case once, without skips or failures")


def controls():
    names = ["native case", BROWSER + "completion", BROWSER + "execution"]
    listing = "\n".join("  " + name for name in names) + "\n\x1b[32mDiscovered 3 tests.\n\x1b[m\n"
    assert discovered(listing) == names
    for incomplete in [listing.replace("3 tests.", "4 tests."), listing.split("\x1b")[0]]:
        try:
            discovered(incomplete)
        except ValueError:
            pass
        else:
            raise AssertionError("Incomplete discovery was admitted")
    scopes = partition(names)
    assert set(scopes["native"]) | set(scopes["browser"]) == set(names)
    assert not set(scopes["native"]) & set(scopes["browser"])
    for invalid in [[], names[:-1], names + [names[0]], names[1:]]:
        try:
            partition(invalid)
        except ValueError:
            pass
        else:
            raise AssertionError("Incomplete or ambiguous discovery was admitted")
    for results in [[], [("other", "Passed")], [("native case", "NotExecuted")],
                    [("native case", "Passed"), ("native case", "Passed")]]:
        document = ET.Element("TestRun")
        for name, outcome in results:
            ET.SubElement(document, "UnitTestResult", testName=name, outcome=outcome)
        try:
            completed(scopes["native"], document)
        except ValueError:
            pass
        else:
            raise AssertionError("Missing, wrong, skipped or repeated execution was admitted")
    document = ET.fromstring('<TestRun><UnitTestResult testName="native case" outcome="Passed" /></TestRun>')
    completed(scopes["native"], document)


def main():
    controls()
    if sys.argv[1:] == ["self-test"]:
        return
    if len(sys.argv) == 5 and sys.argv[1] == "collect":
        project, scope, destination = sys.argv[2:]
        if scope not in {"native", "browser"}:
            raise ValueError("Acceptance scope must be native or browser")
        result = subprocess.run([
            "dotnet", "test", "--project", project, "--configuration", "Release",
            "--no-build", "--no-restore", "--list-tests",
        ], capture_output=True, text=True, timeout=30,
            env=os.environ | {"NO_COLOR": "1"})
        directory = Path(destination).parent
        (directory / "discovery.txt").write_text(result.stdout)
        (directory / "discovery.stderr.txt").write_text(result.stderr)
        result.check_returncode()
        names = discovered(result.stdout)
        scopes = partition(names)
        Path(destination).write_text(json.dumps({"scope": scope, "selected": scopes[scope],
                                                "discovered": len(names)}, indent=2) + "\n")
        print(f"Acceptance discovery: {len(names)} = {len(scopes['native'])} native + "
              f"{len(scopes['browser'])} browser; selected complete {scope} scope", file=sys.stderr)
        # MTP lists the whole inventory even with --filter; check execution against it afterward.
        print("FullyQualifiedName" + ("!~" if scope == "native" else "~") + BROWSER[:-1])
    elif len(sys.argv) == 4 and sys.argv[1] == "check":
        manifest = json.loads(Path(sys.argv[2]).read_text())
        completed(manifest["selected"], ET.parse(sys.argv[3]).getroot())
        print(f"Complete acceptance/{manifest['scope']}: {len(manifest['selected'])} passed once")
    else:
        raise ValueError("Use collect PROJECT native|browser MANIFEST, check MANIFEST TRX, or self-test")


if __name__ == "__main__":
    main()
