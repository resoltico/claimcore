"""Validate comparable workloads and judge independent, alternating growth runs."""

import math
import hashlib
import json
import statistics


REQUIRED_WRITES = {"write/10000/1", "write/100000/1", "write/100000/16"}
REQUIRED_CARDINALITY = {"cardinality/1000"}
BASE_READS = {"read/filtered/100000", "read/search/100000"}


def dictionary_differences(left, right, path):
    changes = []
    for key in sorted(left.keys() | right.keys()):
        if key not in left or key not in right:
            changes.append(f"{path}.{key}")
        else:
            changes.extend(differences(left[key], right[key], f"{path}.{key}"))
    return changes


def differences(left, right, path="$"):
    if isinstance(left, dict) and isinstance(right, dict):
        return dictionary_differences(left, right, path)
    if isinstance(left, list) and isinstance(right, list) and len(left) == len(right):
        return [
            difference
            for index, (a, b) in enumerate(zip(left, right))
            for difference in differences(a, b, f"{path}[{index}]")
        ]
    return [] if type(left) is type(right) and left == right else [path]


def validate_fingerprint(reference, candidate, equivalence):
    fingerprint = candidate.get("workloadFingerprint")
    if not fingerprint or not fingerprint.get("sha256"):
        raise ValueError("Candidate workload has no fingerprint")
    validate_digest(fingerprint)
    previous = reference.get("workloadFingerprint")
    if previous is None:
        if equivalence != fingerprint["sha256"]:
            raise ValueError("Historical workload needs a matching recorded manual equivalence review")
        return "manual equivalence review"
    validate_digest(previous)
    changed = differences(previous["definition"], fingerprint["definition"])
    if previous["sha256"] != fingerprint["sha256"] or changed:
        raise ValueError(
            f"Workload fingerprint mismatch: {previous['sha256']} != {fingerprint['sha256']}; "
            f"differing members: {', '.join(changed) or '(digest)'}"
        )
    return "matching fingerprints"


def validate_digest(fingerprint):
    canonical = fingerprint["canonicalJson"]
    definition = json.loads(canonical)
    if definition != fingerprint["definition"]:
        raise ValueError("Fingerprint canonical JSON differs from its workload definition")
    if hashlib.sha256(canonical.encode("utf-8")).hexdigest() != fingerprint["sha256"]:
        raise ValueError("Workload fingerprint digest is invalid")


def metric_result(name, reference, candidate):
    if len(reference) != 3 or len(candidate) != 3:
        raise ValueError("A metric requires exactly three alternating pairs")
    if any(not math.isfinite(value) or value <= 0 for value in reference + candidate):
        raise ValueError(f"Metric {name} requires finite, positive samples")
    before, after = statistics.median(reference), statistics.median(candidate)
    ratio = after / before
    throughput = name in ("writesPerSecond", "requestsPerSecond")
    passed = ratio >= 0.85 if throughput else ratio <= 1.3
    return {
        "metric": name,
        "referenceRuns": reference,
        "candidateRuns": candidate,
        "referenceMedian": before,
        "candidateMedian": after,
        "ratio": ratio,
        "rule": ">= 0.85" if throughput else "<= 1.30",
        "passed": passed,
    }


def validate_runs(runs):
    if len(runs) != 6 or [run["side"] for run in runs] != ["reference", "candidate"] * 3:
        raise ValueError("Runs must be reference/candidate alternately for three pairs")
    for run in runs:
        required = REQUIRED_WRITES | REQUIRED_CARDINALITY
        if run["hasReadWorkloads"]:
            required |= BASE_READS
        missing = required - run["workloads"].keys()
        if missing:
            raise ValueError(f"Missing required workloads: {sorted(missing)}")
    keys = set(runs[0]["workloads"])
    candidate_keys = set(runs[1]["workloads"])
    for run in runs[::2]:
        if set(run["workloads"]) != keys:
            raise ValueError("Reference workload set changed between runs")
    for run in runs[1::2]:
        if set(run["workloads"]) != candidate_keys:
            raise ValueError("Candidate workload set changed between runs")
    return keys, candidate_keys


def compare(runs, equivalence):
    keys, candidate_keys = validate_runs(runs)
    results = []
    for key in sorted(keys & candidate_keys):
        witnesses = []
        for reference, candidate in zip(runs[::2], runs[1::2]):
            witnesses.append(validate_fingerprint(
                reference["workloads"][key], candidate["workloads"][key], equivalence.get(key)
            ))
        metrics = ["verificationMilliseconds"] if key.startswith("cardinality/") else [
            "p95Milliseconds", "p99Milliseconds"
        ]
        if key.startswith("write/"):
            metrics += ["writesPerSecond", "verificationMilliseconds"]
        elif key.startswith("read/"):
            metrics += ["requestsPerSecond"]
        for metric in metrics:
            values = [run["workloads"][key][metric] for run in runs]
            result = metric_result(metric, values[::2], values[1::2])
            results.append({"workload": key, "identity": witnesses[0], **result})
    return {
        "passed": all(result["passed"] for result in results),
        "metrics": results,
        "notApplicable": sorted(keys ^ candidate_keys),
    }


def markdown(report):
    lines = [
        "# Same-host growth comparison", "",
        f"Reference: `{report['reference']}`; candidate: `{report['candidate']}`.", "",
        "Medians of three alternating pairs. This is relative same-session evidence, not an SLA.", "",
        "| Workload | Metric | Reference median | Candidate median | Ratio | Rule | Result |",
        "| --- | --- | ---: | ---: | ---: | --- | --- |",
    ]
    for row in report.get("metrics", []):
        lines.append(
            f"| {row['workload']} | {row['metric']} | {row['referenceMedian']:.3f} | "
            f"{row['candidateMedian']:.3f} | {row['ratio']:.3f} | {row['rule']} | "
            f"{'PASS' if row['passed'] else 'FAIL'} |"
        )
    lines.append("")
    for row in report.get("metrics", []):
        lines.append(f"- {row['workload']} / {row['metric']}: reference `{row['referenceRuns']}`; candidate `{row['candidateRuns']}`.")
    for key in report.get("notApplicable", []):
        lines.append(f"\nNot applicable on both revisions: `{key}`.")
    if "error" in report:
        lines.append(f"\nFailed: {report['error']}")
    return "\n".join(lines) + "\n"
