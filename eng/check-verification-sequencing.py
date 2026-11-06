#!/usr/bin/env python3
"""Keep expensive comparison and published qualification behind their prerequisites."""
from pathlib import Path
from contextlib import chdir
from tempfile import TemporaryDirectory
from unittest.mock import patch
import os
import textwrap
import re
import sys


def job(workflow, name):
    match = re.search(rf"^  {re.escape(name)}:\n(.*?)(?=^  [a-z][a-z_-]*:\n|\Z)", workflow, re.M | re.S)
    return match.group(1) if match else ""


def dependencies(workflow, name):
    # Only the workflow's static needs field: scalar, flow list or block list.
    # Actionlint validates the surrounding YAML and referenced job identities.
    body = job(workflow, name)
    match = re.search(r"^    needs:([^\n]*)(?:\n((?:      -[^\n]*\n)*))", body, re.M)
    if not match:
        return []
    value = match[1].split("#", 1)[0].strip()
    if value:
        return [item.strip().strip("\"'") for item in value.strip("[]").split(",")]
    return [line.strip()[1:].split("#", 1)[0].strip().strip("\"'")
            for line in match[2].splitlines()]


def depends_on(workflow, name, prerequisite, visited=None):
    visited = set() if visited is None else visited
    if name in visited:
        return False
    visited.add(name)
    return any(parent == prerequisite or depends_on(workflow, parent, prerequisite, visited)
               for parent in dependencies(workflow, name))


def violations(workflow):
    comparison = job(workflow, "comparison")
    acceptance = job(workflow, "acceptance")
    failures = []
    if not depends_on(workflow, "comparison", "gate"):
        failures.append("Full comparison must depend on the ordinary Gate.")
    if "needs.gate.result == 'success'" not in comparison:
        failures.append("Full comparison must require Gate success.")
    for name in ["acceptance", "acceptance_native"]:
        if not depends_on(workflow, name, "frontend"):
            failures.append(f"Published qualification {name} must wait for frontend checks.")
    for name in ["configuration", "lint", "unit", "integration", "web", "frontend", "acceptance_native", "acceptance"]:
        if not depends_on(workflow, "gate", name):
            failures.append(f"Gate must require qualification job {name}.")
    if not re.search(r"(?<![\w-])acceptance-native(?![\w-])", job(workflow, "acceptance_native")):
        failures.append("Native published acceptance must have one complete CI owner.")
    if not re.search(r"(?<![\w-])acceptance-browser(?![\w-])", acceptance):
        failures.append("Each engine must run only complete browser acceptance.")
    if not re.search(r"^\s+CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete$", acceptance, re.M):
        failures.append("CI browser qualification must use complete scope.")
    diagnostic = job(workflow, "browser_diagnostic")
    if "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic" not in diagnostic:
        failures.append("The manual browser diagnostic must declare partial scope.")
    if "if: always() && inputs.browser_spec == ''" not in job(workflow, "gate"):
        failures.append("A diagnostic dispatch must not run the qualification Gate.")
    for name in ["configuration", "lint", "unit", "integration", "web", "frontend", "acceptance_native", "acceptance"]:
        if "if: inputs.browser_spec == ''" not in job(workflow, name):
            failures.append(f"Diagnostic dispatch must not also start qualification job {name}.")
    engine_guard = "(inputs.browser_engine == '' || inputs.browser_engine == 'chromium')"
    for name in ["lint", "unit", "integration", "growth", "web", "frontend", "acceptance_native", "acceptance", "comparison"]:
        if "inputs.browser_case == ''" not in job(workflow, name):
            failures.append(f"Case-only input must not start qualification job {name}.")
        if engine_guard not in job(workflow, name):
            failures.append(f"Invalid engine-only input must not start qualification job {name}.")
    if 'options: [chromium, firefox, webkit]' not in workflow:
        failures.append("Diagnostic engine choices must remain closed.")
    if "-${{ inputs.browser_engine || 'chromium' }}" not in workflow:
        failures.append("Diagnostic concurrency must distinguish engines.")
    if diagnostic.find("python3 -") > diagnostic.find('- uses: ./.github/actions/toolchain'):
        failures.append("Diagnostic preflight must precede toolchain setup.")
    if "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: ${{ env.BROWSER_ENGINE }}" not in diagnostic:
        failures.append("Native diagnostic must use the validated engine.")
    bootstrap = re.search(r"^.*playwright install[^\n]*chromium.*$", diagnostic, re.M)
    selected = re.search(r'^.*playwright install[^\n]*"\$BROWSER_ENGINE".*$', diagnostic, re.M)
    if not bootstrap or not selected or bootstrap.start() > selected.start():
        failures.append("Install Chromium CLI bootstrap before the selected diagnostic engine.")
    if 'if [[ "$BROWSER_ENGINE" != "chromium" ]]; then' not in diagnostic:
        failures.append("Non-Chromium diagnostics must install their selected engine.")
    if 'export CLAIMCORE_ACCEPTANCE_BROWSER_CASE="$BROWSER_CASE"' not in diagnostic:
        failures.append("Diagnostic Native case must be passed as a quoted environment value.")
    return failures


def preflight_controls(workflow):
    def script(name):
        match = re.search(r"python3 - <<'PYTHON'\n(.*?)^          PYTHON", job(workflow, name), re.M | re.S)
        assert match, f"Missing actual preflight in {name}"
        return textwrap.dedent(match[1])

    def admitted(code, values):
        with patch.object(os, "environ", values):
            try:
                exec(code, {})
            except ValueError:
                return False
        return True

    diagnostic = script("browser_diagnostic")
    qualification = script("configuration")
    with TemporaryDirectory(prefix="claimcore-ci-engine-") as directory, chdir(directory):
        specs = Path("web/e2e")
        specs.mkdir(parents=True)
        (specs / "selected.spec.ts").touch()
        values = {"BROWSER_SPEC": "selected.spec.ts", "COMPARISON_REF": "", "GROWTH": "false", "BROWSER_CASE": ""}
        for engine in ["chromium", "firefox", "webkit"]:
            assert admitted(diagnostic, values | {"BROWSER_ENGINE": engine})
            assert admitted(qualification, {"BROWSER_ENGINE": engine, "BROWSER_CASE": ""}) == (engine == "chromium")
        for engine in ["", "unknown", "--project=webkit", "webkit\n"]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": engine})
            assert not admitted(qualification, {"BROWSER_ENGINE": engine, "BROWSER_CASE": ""})
        for name in ["", "absent.spec.ts", "../selected.spec.ts", "*.spec.ts", "selected.spec.ts\n"]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit", "BROWSER_SPEC": name})
        for conflict in [{"GROWTH": "true"}, {"COMPARISON_REF": "main"}]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit"} | conflict)
        for case in ["en 640px green light at 200% zoom", "literal .+?", "🚗" * 256]:
            assert admitted(diagnostic, values | {"BROWSER_ENGINE": "chromium", "BROWSER_CASE": case})
            assert not admitted(qualification, {"BROWSER_ENGINE": "chromium", "BROWSER_CASE": case})
        for case in [" ", " padded", "padded ", "newline\ncase", "nul\x00case", "🚗" * 257]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "chromium", "BROWSER_CASE": case})
        (specs / "nested").mkdir()
        (specs / "nested/selected.spec.ts").touch()
        assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit"})


def controls(workflow):
    assert not violations(workflow)
    # Equivalent safe YAML/dependency graphs must not depend on incidental spelling.
    for replacement in ["    needs: frontend", "    needs:\n      - frontend", "    needs: [frontend, unit]"]:
        assert not violations(workflow.replace("    needs: [frontend]", replacement)), replacement
    indirect = workflow.replace("    needs: [frontend]", "    needs: [web]")
    indirect = indirect.replace("  web:\n", "  web:\n    needs: [frontend]\n")
    assert not violations(indirect)
    changes = [
        ("    needs: [gate]", "    needs: [configuration]"),
        ("needs.gate.result == 'success'", "true"),
        ("    needs: [frontend]", "    needs: [configuration]"),
        ("frontend, acceptance_native, acceptance]", "frontend, acceptance]"),
        ("eng/verify.sh browsers acceptance-native", "eng/verify.sh browsers acceptance"),
        ("eng/verify.sh browsers acceptance-browser", "eng/verify.sh browsers acceptance"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete"),
        ("if: always() && inputs.browser_spec == ''", "if: always()"),
        ("  unit:\n    if: inputs.browser_spec == '' && (inputs.browser_engine == '' || inputs.browser_engine == 'chromium') && inputs.browser_case == ''\n", "  unit:\n"),
    ]
    engine_guard = " && (inputs.browser_engine == '' || inputs.browser_engine == 'chromium')"
    for name in ["lint", "unit", "web", "acceptance_native", "acceptance"]:
        original = job(workflow, name)
        assert violations(workflow.replace(original, original.replace(engine_guard, ""))), name
    for name in ["lint", "unit", "web", "acceptance_native", "acceptance"]:
        original = job(workflow, name)
        assert violations(workflow.replace(original, original.replace(" && inputs.browser_case == ''", ""))), name
    changes.extend([
        ('CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: ${{ env.BROWSER_ENGINE }}', 'CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: chromium'),
        ('options: [chromium, firefox, webkit]', 'options: [chromium, firefox]'),
        ("-${{ inputs.browser_engine || 'chromium' }}", ""),
        ('          npm --prefix web exec -- playwright install --with-deps --only-shell chromium\n', ''),
        ('if [[ "$BROWSER_ENGINE" != "chromium" ]]; then', 'if [[ "$BROWSER_ENGINE" == "chromium" ]]; then'),
    ])
    for before, after in changes:
        assert before in workflow
        assert violations(workflow.replace(before, after)), f"Sequencing regression was not rejected: {before}"


if __name__ == "__main__":
    workflow = (Path(__file__).resolve().parent.parent / ".github/workflows/ci.yml").read_text()
    errors = violations(workflow)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        raise SystemExit(1)
    controls(workflow)
    preflight_controls(workflow)
    print("Verification sequencing: scope graph, equivalent needs syntax, negative controls and actual preflight controls passed.")
