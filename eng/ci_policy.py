"""Select qualification work and validate the required GitHub check."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys


ALWAYS_REQUIRED = {"configuration", "lint", "unit", "release_artifacts"}
PRODUCT_CHECKS = {"integration", "server", "frontend", "acceptance_native", "acceptance"}
CONDITIONAL_CHECKS = {"advisories", "compose_restart"}


def documentation(path):
    if path in {"LICENSE", "PROJECT-NOTICE.md", "LICENSE-COMMERCIAL.md"}:
        return False
    return path.startswith("docs/") or (
        path.endswith(".md") and not path.startswith(("src/", "web/", "tests/"))
    )


def package_input(path):
    project_input = "/" not in path or path.startswith(("src/", "tests/", "eng/"))
    return (
        project_input and path.endswith((".fsproj", ".props", ".targets", ".slnx", ".sln", "/packages.lock.json"))
    ) or path in {
        "global.json", "NuGet.Config", ".node-version", "web/package.json",
        "web/package-lock.json", "web/.npmrc",
    }


def dependency_input(path):
    return package_input(path) or (
        path in {
            ".github/workflows/ci.yml", "eng/verify.sh", "eng/ci_policy.py", "eng/validate_advisories.py",
        }
        or path.startswith(".github/actions/toolchain/")
    )


def compose_input(path):
    # The image restores the entire solution before publishing the server and browser.
    return package_input(path) or path.startswith(
        ("deployment/", "db/", "src/", "web/", "contracts/", "eng/licenses/")
    ) or path in {
        ".dockerignore", "eng/compose-restart-smoke.sh", "eng/source-archive.sh", "eng/ci_policy.py",
        ".github/workflows/ci.yml", ".github/workflows/compose.yml",
    }


def qualification(event, paths):
    full = event != "pull_request"
    return {
        "docs_only": not full and bool(paths) and all(map(documentation, paths)),
        "advisories": full or any(map(dependency_input, paths)),
        "compose_restart": full or any(map(compose_input, paths)),
    }


def gate_failures(results, scope):
    required = ALWAYS_REQUIRED | (PRODUCT_CHECKS if not scope["docs_only"] else set())
    required |= {job for job in CONDITIONAL_CHECKS if scope[job]}
    expected = ALWAYS_REQUIRED | PRODUCT_CHECKS | CONDITIONAL_CHECKS
    failures = []
    for missing in sorted(expected - results.keys()):
        failures.append(f"Missing Gate dependency: {missing}")
    for extra in sorted(results.keys() - expected):
        failures.append(f"Unexpected Gate dependency: {extra}")
    for name in sorted(expected & results.keys()):
        expected_result = "success" if name in required else "skipped"
        actual = results[name].get("result")
        print(f"{name}: {actual}")
        if actual != expected_result:
            failures.append(f"{name} must be {expected_result}, got {actual}.")
    return failures


def plan():
    event = os.environ["EVENT_NAME"]
    full = event != "pull_request"
    paths = []
    if not full:
        change = os.environ["BASE_SHA"] + "..." + os.environ["HEAD_SHA"]
        raw_paths = subprocess.check_output(["git", "diff", "--name-only", "--no-renames", "-z", change])
        paths = [os.fsdecode(path) for path in raw_paths.split(b"\0") if path]
    platforms = [{"os": "ubuntu-24.04", "rid": "linux-x64"}]
    chromium_shards = 3
    engines = [
        {"engine": "chromium", "shard": f"{index}/{chromium_shards}"}
        for index in range(1, chromium_shards + 1)
    ]
    if full:
        platforms += [{"os": "ubuntu-24.04", "rid": "linux-musl-x64"}, {"os": "macos-15", "rid": "osx-arm64"}]
        engines += [{"engine": engine, "shard": "1/1"} for engine in ("firefox", "webkit")]
    dockerfile = Path("deployment/Dockerfile").read_text(encoding="utf-8")
    images = re.findall(r"^FROM (node:[^\s]+@sha256:[0-9a-f]{64}) AS node$", dockerfile, re.M)
    if len(images) != 1:
        raise ValueError("Expected one pinned Node stage in deployment/Dockerfile.")
    outputs = {key: str(value).lower() for key, value in qualification(event, paths).items()}
    outputs.update(
        platforms=json.dumps({"include": platforms}, separators=(",", ":")),
        engines=json.dumps({"include": engines}, separators=(",", ":")),
        node_image=images[0],
    )
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in outputs.items():
            output.write(f"{key}={value}\n")


def gate():
    scope = {}
    for key in ("docs_only", *sorted(CONDITIONAL_CHECKS)):
        value = os.environ[key.upper()]
        if value not in ("true", "false"):
            raise ValueError(f"Invalid configuration output for {key}.")
        scope[key] = value == "true"
    failures = gate_failures(json.loads(os.environ["RESULTS"]), scope)
    if failures:
        sys.exit("\n".join(failures))


if __name__ == "__main__":
    if sys.argv[1:] == ["plan"]:
        plan()
    elif sys.argv[1:] == ["gate"]:
        gate()
    else:
        sys.exit("Usage: python3 eng/ci_policy.py plan|gate")
