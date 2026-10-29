#!/usr/bin/env python3
"""Run source-stable, isolated, same-host growth comparisons; preserve all evidence."""

import json
import hashlib
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import uuid

from growth_comparison import compare, markdown, validate_fingerprint
from growth_reference import prepare


PROJECT = "tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj"
TEST = "FullyQualifiedName~synthetic write contention and verifier growth qualification"


def git(root, *arguments):
    return subprocess.check_output(["git", "-C", str(root), *arguments], text=True).strip()


def candidate_snapshot(root):
    revision = git(root, "rev-parse", "HEAD")
    if git(root, "ls-files", "--others", "--exclude-standard"):
        raise ValueError("Stage new candidate source files before comparison; untracked files cannot be snapshotted")
    arguments = ["git", "-C", str(root), "diff", "--binary", "--no-ext-diff", "--no-textconv",
                 "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "HEAD"]
    patch = subprocess.check_output(arguments)
    if git(root, "rev-parse", "HEAD") != revision or subprocess.check_output(arguments) != patch:
        raise ValueError("Candidate source changed while capturing its snapshot; retry with stable inputs")
    digest = hashlib.sha256(patch).hexdigest() if patch else None
    return revision, patch, digest


def execute(arguments, root, log, environment):
    with log.open("wb") as output:
        process = subprocess.Popen(
            arguments, cwd=root, env=environment, stdout=output,
            stderr=subprocess.STDOUT, start_new_session=True,
        )
        try:
            code = process.wait(timeout=7200)
        except BaseException:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=15)
            raise
    if code:
        raise RuntimeError(f"Command exited {code}; full output: {log}")


def owned_containers(owner):
    return subprocess.check_output([
        "docker", "ps", "-aq", "--filter", f"label=claimcore.growth-compare={owner}",
    ], text=True).split()


def clean_containers(owner):
    containers = owned_containers(owner)
    if containers:
        subprocess.run(["docker", "rm", "-f", *containers], check=True, capture_output=True)
    if owned_containers(owner):
        raise RuntimeError("Owned comparison containers remain after cleanup")


def validate_completion(state, preparation):
    if state["result"] != "completed" or state["completed"] != 3 or state["expected"] != 3:
        raise ValueError(f"Incomplete comparison write profiles: {state}")
    expected_reads = state.get("requiredReadProfiles", 0)
    if preparation["hasReadWorkloads"] and expected_reads < 2:
        raise ValueError("The reference or candidate omitted its base read workloads")
    if state.get("completedReadProfiles", 0) != expected_reads:
        raise ValueError(f"Incomplete comparison read profiles: {state}")
    return expected_reads


def retain_failed_reports(growth, destination):
    if growth.exists():
        shutil.copytree(growth, destination, ignore=shutil.ignore_patterns("keys-*"))


def collect(root, destination, preparation):
    growth = root / "artifacts/verification/growth"
    states = list(growth.glob("*/run.json"))
    if len(states) != 1:
        raise ValueError("A comparison run must produce exactly one growth report")
    source = states[0].parent
    shutil.copytree(source, destination / "growth")
    expected_reads = validate_completion(json.loads(states[0].read_text()), preparation)
    workloads = {}
    for path in sorted(source.glob("workload-*.json")):
        report = json.loads(path.read_text())
        business = report["business"]
        key = f"write/{business['writes']}/{business['writers']}"
        workloads[key] = {**business, "workloadFingerprint": report.get("workloadFingerprint")}
    for path in sorted(source.glob("read-workload-*.json")):
        report = json.loads(path.read_text())["workload"]
        report["requestsPerSecond"] = report["requests"] / report["seconds"]
        workloads[f"read/{report['name']}/{report['cases']}"] = report
    cardinality = json.loads((source / "cardinality.json").read_text())["profiles"]
    if len(cardinality) != 1 or cardinality[0]["cases"] != 1000:
        raise ValueError("Missing or unexpected cardinality comparison")
    workloads["cardinality/1000"] = cardinality[0]
    if len(workloads) != 4 + expected_reads:
        raise ValueError("Duplicated or unexpected comparison workloads")
    return {"hasReadWorkloads": preparation["hasReadWorkloads"], "workloads": workloads}


def run_workloads(checkout, destination, environment, preparation):
    destination.mkdir()
    growth = checkout / "artifacts/verification/growth"
    if growth.exists():
        shutil.rmtree(growth)  # Only the owned, disposable checkout's output.
    selected = dict(environment)
    selected["CLAIMCORE_GROWTH_COMPARISON"] = "1"
    selected["CLAIMCORE_TEST_REPORT_DIRECTORY"] = str(destination)
    try:
        execute([
            "dotnet", "test", "--project", PROJECT, "--configuration", "Release", "--no-build",
            "--no-restore", "--zero-tests-policy=strict", "--timeout=110m", "--filter", TEST,
            "--report-trx", f"--report-trx-filename={destination / 'results.trx'}", "--",
            f"--settings={checkout / 'eng/expecto.integration.runsettings'}",
        ], checkout, destination / "test.log", selected)
    except BaseException:
        retain_failed_reports(growth, destination / "growth-failure")
        raise
    return collect(checkout, destination, preparation)


def review_equivalence(root, revisions, patch_digest):
    has_fingerprint = subprocess.run([
        "git", "-C", str(root), "cat-file", "-e",
        revisions["reference"] + ":tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs",
    ], capture_output=True).returncode == 0
    if has_fingerprint:
        return None
    path = os.environ.get("CLAIMCORE_GROWTH_EQUIVALENCE_FILE")
    if not path:
        raise ValueError(
            "Reference predates workload fingerprints. Record a manual act-cycle/population/field/query "
            "equivalence review, then set CLAIMCORE_GROWTH_EQUIVALENCE_FILE to its JSON path. "
            "Required members: reference, candidate (exact commit IDs), workloads (reviewed keys), review."
        )
    review = json.loads(Path(path).read_text())
    if any(review.get(side) != revision for side, revision in revisions.items()):
        raise ValueError("Manual equivalence review is for different source commits")
    if review.get("candidatePatchSha256") != patch_digest:
        raise ValueError("Manual equivalence review is for a different candidate source patch")
    if not isinstance(review.get("review"), str) or not review["review"].strip():
        raise ValueError("Manual equivalence review must explain the checked semantics")
    if not isinstance(review.get("workloads"), list) or not review["workloads"]:
        raise ValueError("Manual equivalence review must identify the reviewed workloads")
    return review


def prepare_checkouts(root, workspace, report, owner, candidate_patch):
    preparations = {}
    for side in ("reference", "candidate"):
        checkout = workspace / side
        subprocess.run([
            "git", "-C", str(root), "worktree", "add", "--detach", str(checkout), report[side],
        ], check=True, capture_output=True)
        if side == "candidate" and candidate_patch:
            subprocess.run(["git", "-C", str(checkout), "apply", "--binary", "-"],
                           input=candidate_patch, check=True, capture_output=True)
        preparations[side] = prepare(checkout, owner)
        (Path(report["directory"]) / f"{side}-harness.patch").write_text(preparations[side]["patch"])
        print(f"Building isolated {side} {report[side]}", flush=True)
        for action, arguments in (
            ("restore", ["restore", PROJECT, "--locked-mode"]),
            ("build", ["build", PROJECT, "--configuration", "Release", "--no-restore"]),
        ):
            execute(["dotnet", *arguments], checkout,
                    Path(report["directory"]) / f"{side}-{action}.log", os.environ.copy())
    return preparations


def alternate(workspace, report, preparations):
    runs = []
    equivalence = report.get("manualEquivalence")
    for pair in range(3):
        for side in ("reference", "candidate"):
            print(f"Growth pair {pair + 1}/3: {side}", flush=True)
            destination = Path(report["directory"]) / f"pair-{pair + 1}-{side}"
            run = run_workloads(workspace / side, destination, os.environ.copy(), preparations[side])
            runs.append({"side": side, **run})
            report["runs"] = runs
            if len(runs) % 2 == 0:
                for key in runs[-1]["workloads"]:
                    if equivalence and key in equivalence["workloads"]:
                        report.setdefault("reviewedFingerprints", {})[key] = (
                            runs[-1]["workloads"][key]["workloadFingerprint"]["sha256"]
                        )
                for key in runs[-2]["workloads"].keys() & runs[-1]["workloads"].keys():
                    validate_fingerprint(
                        runs[-2]["workloads"][key], runs[-1]["workloads"][key],
                        report.get("reviewedFingerprints", {}).get(key),
                    )
    return compare(runs, report.get("reviewedFingerprints", {}))


def main(reference):
    root = Path(__file__).resolve().parent.parent
    candidate, candidate_patch, patch_digest = candidate_snapshot(root)
    revisions = {
        "reference": git(root, "rev-parse", "--verify", "--end-of-options", reference + "^{commit}"),
        "candidate": candidate,
    }
    if "CLAIMCORE_GROWTH_PROFILE" in os.environ or "CLAIMCORE_GROWTH_COMPARISON" in os.environ:
        raise ValueError("growth-compare owns workload selection; unset growth selection variables")
    review = review_equivalence(root, revisions, patch_digest)
    subprocess.run(["docker", "info", "--format", "{{.ServerVersion}}"], check=True, capture_output=True)
    owner = str(uuid.uuid4())
    directory = Path(os.environ.get("CLAIMCORE_TEST_REPORT_DIRECTORY", root / "artifacts/verification"))
    directory = directory.resolve() / ("growth-compare-" + owner)
    directory.mkdir(parents=True)
    report = {**revisions, "candidatePatchSha256": patch_digest,
              "directory": str(directory), "owner": owner, "manualEquivalence": review}
    (directory / "candidate-source.patch").write_bytes(candidate_patch)
    print(f"Comparison evidence: {directory}", flush=True)
    with tempfile.TemporaryDirectory(prefix="claimcore-growth-compare-") as temporary:
        workspace = Path(temporary)
        try:
            preparations = prepare_checkouts(root, workspace, report, owner, candidate_patch)
            report.update(alternate(workspace, report, preparations))
        except BaseException as error:
            report.update(passed=False, error=str(error) or type(error).__name__)
        finally:
            failures = []
            try:
                clean_containers(owner)
            except Exception as error:
                failures.append(str(error))
            for side in ("reference", "candidate"):
                checkout = workspace / side
                if checkout.exists():
                    result = subprocess.run([
                        "git", "-C", str(root), "worktree", "remove", "--force", str(checkout),
                    ], capture_output=True, text=True)
                    if result.returncode:
                        failures.append(result.stderr)
            report["cleanup"] = {"passed": not failures, "errors": failures}
            if failures:
                report["passed"] = False
            (directory / "comparison.json").write_text(json.dumps(report, indent=2) + "\n")
            table = markdown(report)
            (directory / "comparison.md").write_text(table)
            print(table, flush=True)
    return 0 if report["passed"] else 1


def interrupted(_signal, _frame):
    raise KeyboardInterrupt()


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: growth-compare REF")
        sys.exit(main(sys.argv[1]))
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
