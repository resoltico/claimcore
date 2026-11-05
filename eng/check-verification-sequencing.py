#!/usr/bin/env python3
"""Keep expensive comparison and published qualification behind their prerequisites."""
from pathlib import Path
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
    return failures


def controls(workflow):
    assert not violations(workflow)
    changes = [
        ("    needs: [gate]", "    needs: [configuration]"),
        ("needs.gate.result == 'success'", "true"),
        ("    needs: [frontend]", "    needs: [configuration]"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic"),
        ("CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: diagnostic", "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE: complete"),
        ("if: always() && inputs.browser_spec == ''", "if: always()"),
        ("  unit:\n    if: inputs.browser_spec == ''\n", "  unit:\n"),
    ]
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
    print("Verification sequencing: prerequisites and seven regression controls passed.")
