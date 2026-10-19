"""Independent oracle for ClaimCore 0.10.0 delivery knowledge and reply correlation.

Reads the 0.10.0 policy tables (contracts/delivery-policy.json at df3263f) and writes golden
vectors that the typed F# implementation (ClaimCore.Wire.Delivery / Correlation) and the
hand-written TypeScript twin must reproduce. It deliberately does not import or execute any
ClaimCore code.

Run: python3 -I delivery_oracle.py PATH/TO/delivery-policy.json OUTPUT_DIRECTORY
"""

import itertools
import json
import pathlib
import sys

MAX = 9007199254740991

# Endpoint facts from contracts/catalog.json at df3263f (unchanged by the redesign).
ENDPOINTS = {
    "session": {"mutates": False, "receiptScope": "NONE",
                "tags": ["SUCCEEDED"]},
    "session.logout": {"mutates": False, "receiptScope": "NONE", "tags": ["SUCCEEDED"]},
    "context": {"mutates": False, "receiptScope": "NONE",
                "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "case.get": {"mutates": False, "receiptScope": "NONE",
                 "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "case.list": {"mutates": False, "receiptScope": "NONE",
                  "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "case.history": {"mutates": False, "receiptScope": "NONE",
                     "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "case.preview": {"mutates": False, "receiptScope": "CASE",
                     "tags": ["REVIEWED", "ALREADY_ACCEPTED", "REJECTED", "FAILED", "CANCELLED"]},
    "case.execute": {"mutates": True, "receiptScope": "CASE",
                     "tags": ["ACCEPTED", "ALREADY_ACCEPTED", "REJECTED", "FAILED", "CANCELLED",
                              "OUTCOME_UNKNOWN"]},
    "operation.observe": {"mutates": False, "receiptScope": "NONE",
                          "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "authority.execute": {"mutates": True, "receiptScope": "AUTHORITY",
                          "tags": ["ACCEPTED", "ALREADY_ACCEPTED", "REJECTED", "FAILED",
                                   "CANCELLED", "OUTCOME_UNKNOWN"]},
    "authority.list": {"mutates": False, "receiptScope": "NONE",
                       "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
    "authority.grants": {"mutates": False, "receiptScope": "NONE",
                         "tags": ["SUCCEEDED", "REJECTED", "FAILED", "CANCELLED"]},
}
PHASES = ["NOT_STARTED", "STARTED_UNCONFIRMED", None]
DIAGNOSTICS = ["OPERATION_CONTENT_CONFLICT", "CASE_REVISION_CONFLICT",
               "ACCESS_RESOURCE_UNAVAILABLE"]
KNOWLEDGE = ["NOT_SENT", "READ_SUCCEEDED", "READ_FAILED", "ACCEPTED", "NOT_ACCEPTED",
             "UNCONFIRMED"]
ACCUMULATED = KNOWLEDGE + ["CONTENT_EXCLUDED"]
EVIDENCE = ["NONE", "CONTENT_ACCEPTED", "CONTENT_EXCLUDED"]


class Policy:
    def __init__(self, document):
        self.outcomes = {(r["Item1"], r["Item2"]): r["Item3"] for r in document["outcomes"]}
        self.missing = {(r["Item1"], r["Item2"]): r["Item3"] for r in document["missing"]}
        self.hosts = {(r["Item1"], r["Item2"]): r["Item3"] for r in document["hosts"]}
        self.evidence = [(r["Item1"], r["Item2"], r["Item3"], r["Item4"])
                         for r in document["evidence"]]
        self.resolutions = {r["Item1"]: r["Item2"] for r in document["resolutions"]}
        self.unresolved = document["unresolvedKnowledge"]
        assert document["contentExcludedDiagnostic"] == "OPERATION_CONTENT_CONFLICT"

    def knowledge(self, endpoint, attempt):
        mutates = ENDPOINTS[endpoint]["mutates"]
        kind = attempt["kind"]
        if kind == "NOT_DISPATCHED":
            return self.missing[(False, mutates)]
        if kind == "DISPATCHED_WITHOUT_REPLY":
            return self.missing[(True, mutates)]
        if kind == "HOST_FAILURE":
            return self.hosts[(mutates, attempt["phase"] == "NOT_STARTED")]
        return self.outcomes[(attempt["tag"], mutates)]

    def resolution(self, endpoint, attempt):
        if attempt["kind"] != "OUTCOME" or ENDPOINTS[endpoint]["receiptScope"] == "NONE":
            return "NONE"
        knowledge = self.knowledge(endpoint, attempt)
        diagnostic = attempt.get("diagnostic") or ""
        for expected_knowledge, expected_tag, expected_diagnostic, evidence in self.evidence:
            if all(e == "*" or e == a for e, a in
                   [(expected_knowledge, knowledge), (expected_tag, attempt["tag"]),
                    (expected_diagnostic, diagnostic)]):
                return evidence
        return "NONE"

    def accumulate(self, prior, attempt, evidence):
        if evidence in self.resolutions:
            return self.resolutions[evidence]
        return prior if prior == self.unresolved else attempt


def attempts(endpoint):
    yield {"kind": "NOT_DISPATCHED"}
    yield {"kind": "DISPATCHED_WITHOUT_REPLY"}
    for phase in PHASES:
        yield {"kind": "HOST_FAILURE", "phase": phase}
    for tag in ENDPOINTS[endpoint]["tags"]:
        if tag == "REJECTED":
            for diagnostic in DIAGNOSTICS:
                yield {"kind": "OUTCOME", "tag": tag, "diagnostic": diagnostic}
        else:
            yield {"kind": "OUTCOME", "tag": tag}


# ---------------------------------------------------------------------------------------------
# Correlation: Python re-statement of the 0.10.0 CorrelationPolicy rules over the members they
# read. Documents below are correlation-relevant projections, not complete wire documents.
# ---------------------------------------------------------------------------------------------
RECEIPT = {"ACCEPTED", "ALREADY_ACCEPTED"}


def get(document, path):
    value = document
    for part in path:
        if not isinstance(value, dict) or part not in value:
            return None
        value = value[part]
    return value


def correlated(endpoint, request, reply):
    scope = ENDPOINTS[endpoint]["receiptScope"]
    tag = get(reply, ["outcome", "tag"])
    checks = []
    if "actorId" in request and tag == "SUCCEEDED":
        checks.append(get(reply, ["outcome", "data", "actor", "actorId"]) == request["actorId"])
    if scope in ("CASE", "AUTHORITY"):
        checks.append(reply.get("operationId") == request.get("operationId")
                      and isinstance(request.get("operationId"), str))
    if tag in RECEIPT:
        checks.append(get(reply, ["outcome", "data", "operationId"]) == request.get("operationId"))
        checks.append(get(reply, ["outcome", "data", "scope"]) == scope)
        checks.append(isinstance(get(request, ["act", "kind"]), str)
                      and get(reply, ["outcome", "data", "act"]) == get(request, ["act", "kind"]))
        if scope == "CASE":
            left = get(reply, ["outcome", "data", "caseReference"])
            right = request.get("caseReference")
            checks.append(isinstance(left, str) and isinstance(right, str)
                          and left.upper() == right.upper())
            revision = get(reply, ["outcome", "data", "revision"])
            expected = request.get("expectedRevision")
            checks.append(isinstance(revision, int) and isinstance(expected, int)
                          and expected < MAX and revision == expected + 1)
    if tag == "OUTCOME_UNKNOWN":
        checks.append(get(reply, ["outcome", "data", "operationId"]) == request.get("operationId"))
    if "operationId" in request and "act" not in request and tag == "SUCCEEDED":
        checks.append(get(reply, ["outcome", "data", "operationId"]) == request["operationId"])
    if tag == "REVIEWED":
        left = get(reply, ["outcome", "data", "review", "after", "caseReference"])
        right = request.get("caseReference")
        checks.append(isinstance(left, str) and isinstance(right, str)
                      and left.upper() == right.upper())
        revision = get(reply, ["outcome", "data", "review", "after", "revision"])
        expected = request.get("expectedRevision")
        checks.append(isinstance(revision, int) and isinstance(expected, int)
                      and expected < MAX and revision == expected + 1)
    return all(checks)


OP = "10000000-0000-4000-8000-000000000001"
OTHER_OP = "10000000-0000-4000-8000-000000000002"
ACTOR = "a0000000-0000-4000-8000-000000000001"
OTHER_ACTOR = "a0000000-0000-4000-8000-000000000002"


def case_request(revision=4, reference="Demo-0001", kind="DECIDE"):
    return {"operationId": OP, "caseReference": reference, "expectedRevision": revision,
            "act": {"kind": kind}}


def case_receipt(tag, **overrides):
    data = {"operationId": OP, "scope": "CASE", "act": "DECIDE", "caseReference": "DEMO-0001",
            "revision": 5}
    data.update(overrides)
    return {"operationId": OP, "outcome": {"tag": tag, "data": data}}


def case_correlation_cases(add):
    for tag in ["ACCEPTED", "ALREADY_ACCEPTED"]:
        add(f"case {tag} matches with case-folded reference", "case.execute",
            case_request(), case_receipt(tag))
        add(f"case {tag} envelope operation differs", "case.execute", case_request(),
            dict(case_receipt(tag), operationId=OTHER_OP))
        add(f"case {tag} receipt operation differs", "case.execute", case_request(),
            case_receipt(tag, operationId=OTHER_OP))
        add(f"case {tag} receipt scope differs", "case.execute", case_request(),
            case_receipt(tag, scope="AUTHORITY"))
        add(f"case {tag} act differs", "case.execute", case_request(),
            case_receipt(tag, act="CLOSE"))
        add(f"case {tag} reference differs", "case.execute", case_request(),
            case_receipt(tag, caseReference="DEMO-0002"))
        add(f"case {tag} revision is not the successor", "case.execute", case_request(),
            case_receipt(tag, revision=6))
        add(f"case {tag} at the terminal expected revision", "case.execute",
            case_request(revision=MAX), case_receipt(tag, revision=MAX))
        add(f"case {tag} preview replay matches", "case.preview", case_request(),
            case_receipt(tag))
    for tag in ["REJECTED", "FAILED", "CANCELLED"]:
        add(f"case {tag} echoes the operation", "case.execute", case_request(),
            {"operationId": OP, "outcome": {"tag": tag, "data": {}}})
        add(f"case {tag} echoes another operation", "case.execute", case_request(),
            {"operationId": OTHER_OP, "outcome": {"tag": tag, "data": {}}})
    add("case OUTCOME_UNKNOWN matches", "case.execute", case_request(),
        {"operationId": OP, "outcome": {"tag": "OUTCOME_UNKNOWN", "data": {"operationId": OP}}})
    add("case OUTCOME_UNKNOWN names another operation", "case.execute", case_request(),
        {"operationId": OP, "outcome": {"tag": "OUTCOME_UNKNOWN",
                                        "data": {"operationId": OTHER_OP}}})
    review = {"after": {"caseReference": "demo-0001", "revision": 5}}
    add("preview REVIEWED matches", "case.preview", case_request(),
        {"operationId": OP, "outcome": {"tag": "REVIEWED", "data": {"review": review}}})
    add("preview REVIEWED reference differs", "case.preview", case_request(),
        {"operationId": OP, "outcome": {"tag": "REVIEWED", "data": {
            "review": {"after": {"caseReference": "DEMO-0002", "revision": 5}}}}})
    add("preview REVIEWED revision differs", "case.preview", case_request(),
        {"operationId": OP, "outcome": {"tag": "REVIEWED", "data": {
            "review": {"after": {"caseReference": "DEMO-0001", "revision": 4}}}}})

def authority_correlation_cases(add):
    authority_request = {"operationId": OP, "target": {"kind": "HUMAN"},
                         "act": {"kind": "SET_GRANT"}}
    authority_receipt = {"operationId": OP, "outcome": {"tag": "ACCEPTED", "data": {
        "operationId": OP, "scope": "AUTHORITY", "act": "SET_GRANT"}}}
    add("authority ACCEPTED matches", "authority.execute", authority_request, authority_receipt)
    add("authority ACCEPTED act differs", "authority.execute", authority_request,
        {"operationId": OP, "outcome": {"tag": "ACCEPTED", "data": {
            "operationId": OP, "scope": "AUTHORITY", "act": "SET_ENABLED"}}})
    add("authority ACCEPTED scope differs", "authority.execute", authority_request,
        {"operationId": OP, "outcome": {"tag": "ACCEPTED", "data": {
            "operationId": OP, "scope": "CASE", "act": "SET_GRANT"}}})

def resource_correlation_cases(add):
    grants_request = {"actorId": ACTOR, "limit": 50}
    add("grants page names the requested actor", "authority.grants", grants_request,
        {"outcome": {"tag": "SUCCEEDED", "data": {"actor": {"actorId": ACTOR}}}})
    add("grants page names another actor", "authority.grants", grants_request,
        {"outcome": {"tag": "SUCCEEDED", "data": {"actor": {"actorId": OTHER_ACTOR}}}})
    add("grants refusal is not actor-correlated", "authority.grants", grants_request,
        {"outcome": {"tag": "REJECTED", "data": {}}})
    add("observation names the queried operation", "operation.observe", {"operationId": OP},
        {"outcome": {"tag": "SUCCEEDED", "data": {"operationId": OP}}})
    add("observation names another operation", "operation.observe", {"operationId": OP},
        {"outcome": {"tag": "SUCCEEDED", "data": {"operationId": OTHER_OP}}})
    add("ordinary read needs no correlation", "case.get", {"caseReference": "Demo-0001"},
        {"outcome": {"tag": "SUCCEEDED", "data": {}}})


def correlation_cases():
    cases = []

    def add(name, endpoint, request, reply):
        cases.append({"name": name, "endpoint": endpoint, "request": request, "reply": reply,
                      "correlated": correlated(endpoint, request, reply)})

    case_correlation_cases(add)
    authority_correlation_cases(add)
    resource_correlation_cases(add)
    return cases


def main():
    policy = Policy(json.loads(pathlib.Path(sys.argv[1]).read_text()))
    output = pathlib.Path(sys.argv[2])
    knowledge = []
    evidence = []
    for endpoint in ENDPOINTS:
        for attempt in attempts(endpoint):
            knowledge.append({"endpoint": endpoint, "attempt": attempt,
                              "knowledge": policy.knowledge(endpoint, attempt)})
            evidence.append({"endpoint": endpoint, "attempt": attempt,
                             "evidence": policy.resolution(endpoint, attempt)})
    accumulate = [{"prior": prior, "attempt": attempt, "evidence": ev,
                   "result": policy.accumulate(prior, attempt, ev)}
                  for prior, attempt, ev in itertools.product(ACCUMULATED, KNOWLEDGE, EVIDENCE)]
    document = {
        "purpose": "Golden delivery vectors from the ClaimCore 0.10.0 policy tables; "
                   "regenerate with python3 -I delivery_oracle.py",
        "knowledge": knowledge,
        "evidence": evidence,
        "accumulate": accumulate,
    }
    (output / "delivery-vectors.json").write_text(json.dumps(document, indent=1) + "\n")
    correlation = {
        "purpose": "Correlation vectors restating the ClaimCore 0.10.0 CorrelationPolicy rules "
                   "over correlation-relevant projections of request and reply documents",
        "cases": correlation_cases(),
    }
    (output / "correlation-vectors.json").write_text(json.dumps(correlation, indent=1) + "\n")
    print(len(knowledge), "knowledge,", len(evidence), "evidence,", len(accumulate),
          "accumulate,", len(correlation["cases"]), "correlation vectors")


if __name__ == "__main__":
    main()
