import { it, expect } from "vitest";
import { isMutationUncertain } from "../src/api/service";
import type { ApiResult, EndpointOutcome } from "../src/api/service";
import { operationId } from "./service-ui.fixtures";
it("explicit uncertainty is retained while definite rejection and cancellation remain definite", () => {
  const unknown: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: {
      endpoint: "case.execute",
      operationId: "11111111-1111-4111-8111-111111111111",
      outcome: { tag: "OUTCOME_UNKNOWN", data: { operationId } },
    },
  };
  expect(isMutationUncertain(unknown)).toBe(true);
  expect(
    isMutationUncertain({
      kind: "outcome",
      status: 200,
      value: {
        endpoint: "case.execute",
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { tag: "CANCELLED", data: null },
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
