"""Reject incomplete native advisory reports before interpreting findings."""

import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def expected_frameworks(solution):
    expected = {}
    for declaration in ET.parse(solution).iter("Project"):
        project = (solution.parent / declaration.attrib["Path"]).resolve()
        lock = json.loads(project.with_name("packages.lock.json").read_text(encoding="utf-8"))
        targets = lock["dependencies"]
        frameworks = {target for target in targets if "/" not in target}
        if not frameworks or project in expected:
            raise ValueError(f"Missing frameworks or duplicate project in solution: {project}")
        for target, entries in targets.items():
            framework, separator, _ = target.partition("/")
            if separator:
                # The report is by framework. Refuse uncovered RID-specific package versions.
                base = targets[framework]
                for name, entry in entries.items():
                    if entry["type"] != "Project" and entry["resolved"] != base.get(name, {}).get("resolved"):
                        raise ValueError(f"NuGet advisory report cannot cover RID-specific package {name} in {project} [{target}].")
        expected[project] = frameworks
    if not expected:
        raise ValueError("The solution declares no projects to audit.")
    return expected


def findings(report, expected):
    if type(report.get("version")) is not int or report["version"] != 1:
        raise ValueError("Unsupported NuGet advisory report version.")
    if not {"--vulnerable", "--include-transitive"} <= set(report["parameters"].split()):
        raise ValueError("NuGet did not report a transitive vulnerability audit.")
    sources = report["sources"]
    if not isinstance(sources, list) or not sources or not all(isinstance(source, str) and source for source in sources):
        raise ValueError("NuGet reported no valid package sources.")
    problems = report.get("problems", [])
    if problems != []:
        raise ValueError(f"NuGet reported audit problems: {problems}")
    if not report["projects"]:
        raise ValueError("NuGet reported no audited projects.")

    audited = {}
    vulnerabilities = []
    for project in report["projects"]:
        path = Path(project["path"]).resolve()
        if path in audited:
            raise ValueError(f"NuGet reported a duplicate project: {path}")
        frameworks = set()
        for framework in project["frameworks"]:
            name = framework["framework"]
            if name in frameworks:
                raise ValueError(f"NuGet reported a duplicate framework: {path} [{name}]")
            frameworks.add(name)
            packages = framework["topLevelPackages"] + framework.get("transitivePackages", [])
            for package in packages:
                for advisory in package["vulnerabilities"]:
                    vulnerabilities.append(
                        f"{path} [{name}] {package['id']} {advisory['severity']} {advisory['advisoryurl']}"
                    )
        audited[path] = frameworks

    if audited != expected:
        mismatches = []
        for project in sorted(audited.keys() | expected.keys()):
            actual = audited.get(project)
            required = expected.get(project)
            if actual != required:
                mismatches.append(f"{project}: expected {required}, got {actual}")
        raise ValueError("NuGet advisory report has incomplete coverage.\n" + "\n".join(mismatches))
    return vulnerabilities


if __name__ == "__main__":
    try:
        report = json.load(sys.stdin)
        detected = findings(report, expected_frameworks(Path("ClaimCore.slnx")))
    except (OSError, ValueError, TypeError, AttributeError, KeyError, ET.ParseError) as error:
        sys.exit(f"Invalid NuGet advisory report: {error}")
    if detected:
        sys.exit("\n".join(detected))
