import { expect, it } from "vitest";
import { mutationAccepted, mutationUncertain } from "../src/protocol/delivery";
import { correlateReply } from "../src/protocol/client";
import { corpusCases, wireReply } from "./contract-corpus.fixtures";
import type { EndpointOutcome } from "../src/api/types";
const accepted = wireReply("operation.execute");
const receipt = () => {
  if (accepted.outcome.kind !== "ACCEPTED" || accepted.outcome.data.subject.kind !== "CASE") {
    throw new Error("Missing case receipt fixture.");
  }
  return {
    ...accepted.outcome.data,
    subject: accepted.outcome.data.subject,
  };
};
const { operationId } = receipt();
const request = {
  kind: "CASE",
  operationId,
  caseReference: "Case-Identity",
  expectedRevision: 2,
  act: {
    kind: receipt().subject.act,
  },
};
const reply = () => ({
  operationId,
  outcome: {
    kind: "ACCEPTED",
    data: {
      ...receipt(),
      subject: {
        ...receipt().subject,
        caseReference: "case-identity",
        revision: 3,
      },
    },
  },
});
it("binds accepted subject, operation ID, act, ASCII case identity and successor revision", () => {
  expect(correlateReply("operation.execute", request, reply())).toBe(true);
  const source = reply();
  for (const changed of [
    {
      kind: "AUTHORITY",
    },
    {
      act: "ERASE",
    },
    {
      caseReference: "other",
    },
    {
      revision: 4,
    },
  ]) {
    expect(
      correlateReply("operation.execute", request, {
        ...source,
        outcome: {
          ...source.outcome,
          data: {
            ...source.outcome.data,
            subject: {
              ...source.outcome.data.subject,
              ...changed,
            },
          },
        },
      }),
    ).toBe(false);
  }
  for (const change of [
    {
      ...source,
      operationId: "20000000-0000-4000-8000-000000000002",
    },
    {
      ...source,
      outcome: {
        ...source.outcome,
        data: {
          ...source.outcome.data,
          operationId: "20000000-0000-4000-8000-000000000002",
        },
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
    const result = {
      kind: "outcome" as const,
      value,
      status: 200,
    };
    expect(mutationAccepted(result), sample.id).toBe(
      ["ACCEPTED", "ALREADY_ACCEPTED"].includes(value.outcome.kind),
    );
    expect(mutationUncertain(result), sample.id).toBe(value.outcome.kind === "OUTCOME_UNKNOWN");
  }
});
