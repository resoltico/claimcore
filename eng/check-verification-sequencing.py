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


def violations(workflow):
    comparison = job(workflow, "comparison")
    acceptance = job(workflow, "acceptance")
    failures = []
    if not re.search(r"^    needs: \[gate\]$", comparison, re.M):
        failures.append("Full comparison must depend on the ordinary Gate.")
    if "needs.gate.result == 'success'" not in comparison:
        failures.append("Full comparison must require Gate success.")
    if not re.search(r"^    needs: \[frontend\]$", acceptance, re.M):
        failures.append("Published browser qualification must wait for frontend checks.")
    if not re.search(r"^\s+CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete$", acceptance, re.M):
        failures.append("CI browser qualification must use complete scope.")
    diagnostic = job(workflow, "browser_diagnostic")
    if "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic" not in diagnostic:
        failures.append("The manual browser diagnostic must declare partial scope.")
    if "if: always() && inputs.browser_spec == ''" not in job(workflow, "gate"):
        failures.append("A diagnostic dispatch must not run the qualification Gate.")
    for name in ["configuration", "lint", "unit", "integration", "web", "frontend", "acceptance"]:
        if "if: inputs.browser_spec == ''" not in job(workflow, name):
            failures.append(f"Diagnostic dispatch must not also start qualification job {name}.")
    engine_guard = "(inputs.browser_engine == '' || inputs.browser_engine == 'chromium')"
    for name in ["lint", "unit", "integration", "growth", "web", "frontend", "acceptance", "comparison"]:
        if engine_guard not in job(workflow, name):
            failures.append(f"Invalid engine-only input must not start qualification job {name}.")
    if 'options: [chromium, firefox, webkit]' not in workflow:
        failures.append("Diagnostic engine choices must remain closed.")
    if "-${{ inputs.browser_engine || 'chromium' }}" not in workflow:
        failures.append("Diagnostic concurrency must distinguish engines.")
    binding = "BROWSER_ENGINE: ${{ inputs.browser_engine || 'chromium' }}"
    whitelist = 'if os.environ["BROWSER_ENGINE"] not in {"chromium", "firefox", "webkit"}:'
    qualification = job(workflow, "configuration")
    if binding not in qualification or 'if os.environ["BROWSER_ENGINE"] != "chromium":' not in qualification:
        failures.append("Configuration must refuse engine-only diagnostics before setup.")
    if binding not in diagnostic or whitelist not in diagnostic:
        failures.append("Diagnostic engine input must be validated before setup.")
    if diagnostic.find(whitelist) > diagnostic.find('- uses: ./.github/actions/toolchain'):
        failures.append("Diagnostic engine preflight must precede toolchain setup.")
    if "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: ${{ env.BROWSER_ENGINE }}" not in diagnostic:
        failures.append("Native diagnostic must use the validated engine.")
    installation = (
        '          npm --prefix web exec -- playwright install --with-deps --only-shell chromium\n'
        '          if [[ "$BROWSER_ENGINE" != "chromium" ]]; then\n'
        '            npm --prefix web exec -- playwright install --with-deps "$BROWSER_ENGINE"\n'
        '          fi'
    )
    if installation not in diagnostic:
        failures.append("Install the unconditional Chromium CLI bootstrap before the selected non-Chromium engine.")
    return failures


def preflight_controls(workflow):
    def script(name):
        match = re.search(r"python3 - <<'PYTHON'\n(.*?)^          PYTHON", job(workflow, name), re.M | re.S)
        assert match, f"Missing actual preflight in {name}"
        return textwrap.dedent(match[1])

    def admitted(code, values):
        with patch.dict(os.environ, values, clear=True):
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
        values = {"BROWSER_SPEC": "selected.spec.ts", "COMPARISON_REF": "", "GROWTH": "false"}
        for engine in ["chromium", "firefox", "webkit"]:
            assert admitted(diagnostic, values | {"BROWSER_ENGINE": engine})
            assert admitted(qualification, {"BROWSER_ENGINE": engine}) == (engine == "chromium")
        for engine in ["", "unknown", "--project=webkit", "webkit\n"]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": engine})
            assert not admitted(qualification, {"BROWSER_ENGINE": engine})
        for name in ["", "absent.spec.ts", "../selected.spec.ts", "*.spec.ts", "selected.spec.ts\n"]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit", "BROWSER_SPEC": name})
        for conflict in [{"GROWTH": "true"}, {"COMPARISON_REF": "main"}]:
            assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit"} | conflict)
        (specs / "nested").mkdir()
        (specs / "nested/selected.spec.ts").touch()
        assert not admitted(diagnostic, values | {"BROWSER_ENGINE": "webkit"})


def controls(workflow):
    assert not violations(workflow)
    changes = [
        ("    needs: [gate]", "    needs: [configuration]"),
        ("needs.gate.result == 'success'", "true"),
        ("    needs: [frontend]", "    needs: [configuration]"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete"),
        ("if: always() && inputs.browser_spec == ''", "if: always()"),
        ("  unit:\n    if: inputs.browser_spec == '' && (inputs.browser_engine == '' || inputs.browser_engine == 'chromium')\n", "  unit:\n"),
    ]
    engine_guard = " && (inputs.browser_engine == '' || inputs.browser_engine == 'chromium')"
    for name in ["lint", "unit", "web"]:
        original = job(workflow, name)
        assert violations(workflow.replace(original, original.replace(engine_guard, ""))), name
    changes.extend([
        ('CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: ${{ env.BROWSER_ENGINE }}', 'CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE: chromium'),
        ('if os.environ["BROWSER_ENGINE"] not in {"chromium", "firefox", "webkit"}:', 'if False:'),
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
    print("Verification sequencing: prerequisites, seven original controls, engine binding/guards and actual preflight controls passed.")
