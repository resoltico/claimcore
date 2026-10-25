import { it, expect } from "vitest";
import { isMutationUncertain } from "../src/api/service";
import type { ApiResult, EndpointOutcome } from "../src/api/service";
it("explicit uncertainty is retained while definite rejection and cancellation remain definite", () => {
  const unknown: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: {
      recoveryId: null,
      operationId: "11111111-1111-4111-8111-111111111111",
      outcome: { tag: "OUTCOME_UNKNOWN", data: null },
    },
  };
  expect(isMutationUncertain(unknown)).toBe(true);
  expect(
    isMutationUncertain({
      kind: "outcome",
      status: 200,
      value: {
        recoveryId: null,
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { tag: "FAILED", data: { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" } },
      },
    }),
  ).toBe(false);
  expect(
    isMutationUncertain({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    }),
  ).toBe(true);
});
