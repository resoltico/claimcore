import { expect, it } from "vitest";
import { prepareRequest } from "../src/protocol/preparedRequest";
import {
  beginMutation,
  completeMutation,
  interruptMutation,
} from "../src/interaction/mutationLifecycle";
const operationId = "10000000-0000-4000-8000-000000000001";
const requests = [
  prepareRequest("case.execute", {
    operationId,
    caseReference: "SYNTHETIC",
    expectedRevision: 1,
    act: { kind: "CLOSE" },
  }),
  prepareRequest("authority.execute", {
    operationId,
    target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "synthetic" },
    act: { kind: "REGISTER_ACTOR" },
  }),
];
it("both mutation endpoints preserve immutable uncertainty through interruptions and later refusals", () => {
  for (const prepared of requests) {
    const submitting = beginMutation({ phase: "REVIEWING", prepared }, 1);
    const unknown = interruptMutation(submitting);
    expect(unknown.phase).toBe("OUTCOME_UNKNOWN");
    expect(completeMutation(submitting, 2, "ACCEPTED", "CONTENT_ACCEPTED")).toBe(submitting);
    const retry = beginMutation(unknown, 3);
    const refused = completeMutation(retry, 3, "NOT_ACCEPTED", "NONE");
    expect(refused.phase).toBe("OUTCOME_UNKNOWN");
    expect(refused.prepared).toBe(prepared);
    expect(refused.prepared?.bytes).toBe(prepared.bytes);
    expect(
      completeMutation(beginMutation(refused, 4), 4, "ACCEPTED", "CONTENT_ACCEPTED").phase,
    ).toBe("ACCEPTED");
    expect(
      completeMutation(beginMutation(refused, 5), 5, "NOT_ACCEPTED", "CONTENT_EXCLUDED").phase,
    ).toBe("DEFINITELY_REJECTED");
  }
});
