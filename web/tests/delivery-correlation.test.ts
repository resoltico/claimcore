import { expect, it } from "vitest";
import { mutationAccepted, mutationUncertain } from "../src/protocol/delivery";
import { correlateReply } from "../src/protocol/client";
import { corpusCases, wireReply } from "./contract-corpus.fixtures";
import type { EndpointOutcome } from "../src/api/types";
const accepted = wireReply("operation.execute");
const receipt = () => {
  if (accepted.outcome.tag !== "ACCEPTED" || accepted.outcome.data.scope.kind !== "CASE") {
    throw new Error("Missing case receipt fixture.");
  }
  return { ...accepted.outcome.data, scope: accepted.outcome.data.scope };
};
const { operationId } = receipt();
const request = {
  scope: "CASE",
  operationId,
  caseReference: "Case-Identity",
  expectedRevision: 2,
  act: { kind: receipt().scope.act },
};
const reply = () => ({
  operationId,
  outcome: {
    tag: "ACCEPTED",
    data: {
      ...receipt(),
      scope: { ...receipt().scope, caseReference: "case-identity", revision: 3 },
    },
  },
});
it("binds accepted scope, operation ID, act, ASCII case identity and successor revision", () => {
  expect(correlateReply("operation.execute", request, reply())).toBe(true);
  const source = reply();
  for (const changed of [
    { kind: "AUTHORITY" },
    { act: "ERASE" },
    { caseReference: "other" },
    { revision: 4 },
  ]) {
    expect(
      correlateReply("operation.execute", request, {
        ...source,
        outcome: {
          ...source.outcome,
          data: { ...source.outcome.data, scope: { ...source.outcome.data.scope, ...changed } },
        },
      }),
    ).toBe(false);
  }
  for (const change of [
    { ...source, operationId: "20000000-0000-4000-8000-000000000002" },
    {
      ...source,
      outcome: {
        ...source.outcome,
        data: { ...source.outcome.data, operationId: "20000000-0000-4000-8000-000000000002" },
      },
    },
  ]) {
    expect(correlateReply("operation.execute", request, change)).toBe(false);
  }
});
it("classifies authored outcome examples without a generated delivery oracle", () => {
  for (const sample of corpusCases().filter(
    (item) => item.direction === "response" && item.valid,
  )) {
    const value = sample.value as EndpointOutcome;
    const result = { kind: "outcome" as const, value, status: 200 };
    expect(mutationAccepted(result), sample.id).toBe(
      ["ACCEPTED", "ALREADY_ACCEPTED"].includes(value.outcome.tag),
    );
    expect(mutationUncertain(result), sample.id).toBe(value.outcome.tag === "OUTCOME_UNKNOWN");
  }
});
