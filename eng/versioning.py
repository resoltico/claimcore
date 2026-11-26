"""Read the evaluated product version and check its release inputs."""

import argparse
from datetime import date
import json
import os
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent
VERSION = r"[0-9]+\.[0-9]+\.[0-9]+"


def product_version(root):
    versions = {
        component: subprocess.check_output(
            [
                "dotnet", "msbuild", f"src/ClaimCore.{component}/ClaimCore.{component}.fsproj",
                "-nologo", "-property:Configuration=Release", "-getProperty:Version",
            ],
            cwd=root,
            env=dict(os.environ, DOTNET_NOLOGO="1"),
            text=True,
        ).strip()
        for component in ("Cli", "Server")
    }
    version = versions["Cli"]
    if not re.fullmatch(VERSION, version) or versions["Server"] != version:
        raise ValueError(f"Expected one product version in MAJOR.MINOR.PATCH form. Evaluated {versions}.")
    return version


def check_project_locks(root, version):
    expected = f"[{version}, )"
    count = 0
    stale = []
    for area in ("src", "tests", "eng"):
        for project in sorted((root / area).glob("*/*.fsproj")):
            lock = project.with_name("packages.lock.json")
            for entries in json.loads(lock.read_text(encoding="utf-8"))["dependencies"].values():
                projects = {name.lower() for name, entry in entries.items() if entry["type"] == "Project"}
                for entry in entries.values():
                    for name, requested in entry.get("dependencies", {}).items():
                        if name.lower() in projects:
                            count += 1
                            if requested != expected:
                                stale.append(f"{lock.relative_to(root)}: {name} requests {requested}")
    if count == 0:
        raise ValueError("No first-party project references were found in the NuGet lockfiles.")
    if stale:
        raise ValueError(
            f"{len(stale)} NuGet project references differ from {expected}.\n"
            + "\n".join(stale)
            + "\nRun dotnet restore ClaimCore.slnx --force-evaluate -p:RestoreLockedMode=false and review the diff."
        )


def check_web_toolchain(root):
    package = json.loads((root / "web/package.json").read_text(encoding="utf-8"))
    node = (root / ".node-version").read_text(encoding="utf-8").strip()
    if package["engines"]["node"] != node:
        raise ValueError("web/package.json engines.node must match .node-version.")
    if package["packageManager"] != f"npm@{package['engines']['npm']}":
        raise ValueError("web/package.json packageManager must match engines.npm.")


def release_notes(changelog, tag, version):
    if tag != f"v{version}":
        raise ValueError(f"Tag {tag} differs from the evaluated product version v{version}.")
    lines = changelog.splitlines()
    headings = [(index, line) for index, line in enumerate(lines) if line.startswith("## [")]
    heading_title = f"## [{version}]"
    sections = []
    for (start, heading), (end, _) in zip(headings, headings[1:] + [(len(lines), "")]):
        if heading.startswith(heading_title):
            dated = heading.removeprefix(heading_title + " - ")
            if not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", dated):
                raise ValueError(f"Expected a dated changelog heading for {version}.")
            date.fromisoformat(dated)
            sections.append("\n".join(lines[start + 1 : end]).strip())
    if len(sections) != 1 or not sections[0]:
        raise ValueError(f"Expected exactly one dated changelog section with release notes for {version}.")
    return sections[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("version")
    commands.add_parser("check")
    commands.add_parser("release-notes").add_argument("tag")
    args = parser.parse_args()
    try:
        version = product_version(ROOT)
        if args.command == "version":
            print(version)
        elif args.command == "check":
            check_project_locks(ROOT, version)
            check_web_toolchain(ROOT)
        else:
            print(release_notes((ROOT / "CHANGELOG.md").read_text(encoding="utf-8"), args.tag, version))
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
