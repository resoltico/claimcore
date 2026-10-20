import { expect, it } from "vitest";
import delivery from "@contracts/delivery-vectors.json" with { type: "json" };
import correlation from "@contracts/correlation-vectors.json" with { type: "json" };
import { isEndpointId, correlateReply } from "../src/protocol/client";
import {
  attemptKnowledge,
  resolutionEvidence,
  accumulateKnowledge,
  type AccumulatedKnowledge,
  type AttemptKnowledge,
  type ResolutionEvidence,
} from "../src/protocol/delivery";
import type { ApiResult, EndpointOutcome } from "../src/api/types";
type Attempt = { kind: string; phase?: string | null; tag?: string; diagnostic?: string };
const result = (endpoint: string, attempt: Attempt): ApiResult<EndpointOutcome> => {
  if (attempt.kind === "NOT_DISPATCHED" || attempt.kind === "DISPATCHED_WITHOUT_REPLY") {
    return {
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: attempt.kind !== "NOT_DISPATCHED",
    };
  }
  if (attempt.kind === "HOST_FAILURE") {
    return {
      kind: "hostFailure",
      status: 500,
      failure: { executionPhase: attempt.phase },
    } as unknown as ApiResult<EndpointOutcome>;
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      endpoint,
      operationId: "10000000-0000-4000-8000-000000000001",
      outcome: { tag: attempt.tag, data: { diagnostic: { id: attempt.diagnostic } } },
    },
  } as unknown as ApiResult<EndpointOutcome>;
};
it("matches all 380 frozen independent delivery vectors", () => {
  expect(delivery.knowledge.length + delivery.evidence.length + delivery.accumulate.length).toBe(
    380,
  );
  for (const row of delivery.knowledge) {
    if (!isEndpointId(row.endpoint)) {
      throw new Error("Unknown oracle endpoint.");
    }
    expect(attemptKnowledge(row.endpoint, result(row.endpoint, row.attempt))).toBe(row.knowledge);
  }
  for (const row of delivery.evidence) {
    if (!isEndpointId(row.endpoint)) {
      throw new Error("Unknown oracle endpoint.");
    }
    expect(resolutionEvidence(result(row.endpoint, row.attempt))).toBe(row.evidence);
  }
  for (const row of delivery.accumulate) {
    expect(
      accumulateKnowledge(
        row.prior as AccumulatedKnowledge,
        row.attempt as AttemptKnowledge,
        row.evidence as ResolutionEvidence,
      ),
    ).toBe(row.result);
  }
});
it("matches all 104 frozen independent correlation vectors", () => {
  expect(correlation.cases).toHaveLength(104);
  for (const row of correlation.cases) {
    if (!isEndpointId(row.endpoint)) {
      throw new Error("Unknown oracle endpoint.");
    }
    expect(
      correlateReply(row.endpoint, row.request, { ...row.reply, endpoint: row.endpoint }),
      row.name,
    ).toBe(row.correlated);
  }
});
