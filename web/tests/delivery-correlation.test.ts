import { expect, it } from "vitest";
import {
  attemptKnowledge,
  accumulateKnowledge,
  resolutionEvidence,
} from "../src/protocol/delivery";
import { correlateReply } from "../src/protocol/client";
import { corpusCases, generatedWebValue } from "./contract-corpus.fixtures";
import type { ApiResult, EndpointOutcome } from "../src/api/types";
import { endpoints } from "@contracts/client";
const accepted = generatedWebValue("case.execute");
const receipt = () => {
  if (accepted.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing receipt fixture.");
  }
  return accepted.outcome.data;
};
const { operationId } = receipt();
const request = {
  operationId,
  caseReference: "Case-Identity",
  expectedRevision: 2,
  act: { kind: receipt().act },
};
const reply = () => ({
  ...accepted,
  operationId,
  outcome: { tag: "ACCEPTED", data: { ...receipt(), caseReference: "case-identity", revision: 3 } },
});
it("correlates scope, identity, act, ASCII reference identity and accepted revision independently", () => {
  expect(correlateReply("case.execute", request, reply())).toBe(true);
  const source = reply();
  for (const changed of [
    { operationId: "20000000-0000-4000-8000-000000000002" },
    { scope: "AUTHORITY" },
    { act: "ERASE" },
    { caseReference: "other" },
    { revision: 4 },
  ]) {
    expect(
      correlateReply("case.execute", request, {
        ...source,
        outcome: { ...source.outcome, data: { ...source.outcome.data, ...changed } },
      }),
    ).toBe(false);
  }
  expect(
    correlateReply("case.execute", request, {
      ...source,
      operationId: "20000000-0000-4000-8000-000000000002",
    }),
  ).toBe(false);
});
it("covers every generated endpoint outcome family against an independent finite truth table", () => {
  for (const sample of corpusCases().filter(
    (item) => item.direction === "response" && item.valid,
  )) {
    const value = sample.value as EndpointOutcome;
    const mutation = endpoints.find((item) => item.id === sample.endpoint)!.mutates;
    const { tag } = value.outcome;
    let expected = mutation ? "NOT_ACCEPTED" : "READ_FAILED";
    if (tag === "ACCEPTED" || tag === "ALREADY_ACCEPTED") {
      expected = "ACCEPTED";
    }
    if (tag === "REVIEWED" || tag === "SUCCEEDED") {
      expected = "READ_SUCCEEDED";
    }
    if (tag === "OUTCOME_UNKNOWN") {
      expected = "UNCONFIRMED";
    }
    expect(
      attemptKnowledge(sample.endpoint, { kind: "outcome", value, status: 200 }),
      sample.id,
    ).toBe(expected);
  }
  expect(() =>
    attemptKnowledge("case.execute", {
      kind: "outcome",
      status: 200,
      value: {
        endpoint: "case.execute",
        operationId,
        outcome: { tag: "SYNTHETIC_UNCOVERED", data: null },
      },
    } as unknown as ApiResult<EndpointOutcome>),
  ).toThrow();
});
it("accumulates unresolved attempts until exact content evidence, while observation cannot resolve content", () => {
  for (const attempt of [
    "NOT_SENT",
    "NOT_ACCEPTED",
    "READ_SUCCEEDED",
    "READ_FAILED",
    "UNCONFIRMED",
  ] as const) {
    expect(accumulateKnowledge("UNCONFIRMED", attempt, "NONE")).toBe("UNCONFIRMED");
  }
  expect(accumulateKnowledge("UNCONFIRMED", "ACCEPTED", "CONTENT_ACCEPTED")).toBe("ACCEPTED");
  expect(accumulateKnowledge("UNCONFIRMED", "NOT_ACCEPTED", "CONTENT_EXCLUDED")).toBe(
    "CONTENT_EXCLUDED",
  );
  const observed: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: { endpoint: "operation.observe", outcome: { tag: "SUCCEEDED", data: receipt() } },
  };
  expect(resolutionEvidence(observed)).toBe("NONE");
  const wrongValues = {
    ...request,
    act: { ...request.act, values: { claimantName: "Different" } },
  };
  expect(wrongValues.operationId).toBe(operationId);
  expect(
    accumulateKnowledge(
      "UNCONFIRMED",
      attemptKnowledge("operation.observe", observed),
      resolutionEvidence(observed),
    ),
  ).toBe("UNCONFIRMED");
});
