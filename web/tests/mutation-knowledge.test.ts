import { expect, it } from "vitest";
import { isMutationUncertain } from "../src/api/v3";
import type { ApiResult, EndpointOutcome } from "../src/api/v3";
import { receipt } from "./v3-recovery.fixtures";
import { preparation, operationId } from "./v3-ui.fixtures";

const fault = {
  code: "COMMIT_OUTCOME_UNKNOWN" as const,
  diagnostic: { id: "CORE_COMMIT_OUTCOME_UNKNOWN" as const, parameters: {} },
  message: "Synthetic storage failure",
  recommendedAction: "RECOVER_EXACT" as const,
};
const result = (value: EndpointOutcome): ApiResult<EndpointOutcome> => ({
  kind: "outcome",
  status: 200,
  value,
});

it("preserves core recovery direction in generic and nested mutation faults", () => {
  expect(
    isMutationUncertain(
      result({ endpoint: "recovery.dismiss", outcome: { tag: "FAILED", data: fault } }),
    ),
  ).toBe(true);
  expect(
    isMutationUncertain(
      result({
        endpoint: "command.prepare",
        outcome: {
          tag: "FAILED",
          data: {
            operationId: "00000000-0000-4000-8000-000000000001",
            fault,
          },
        },
      }),
    ),
  ).toBe(true);
  expect(
    isMutationUncertain(
      result({
        endpoint: "recovery.dismiss",
        outcome: {
          tag: "FAILED",
          data: {
            code: "STORE_UNAVAILABLE",
            diagnostic: { id: "CORE_STORE_UNAVAILABLE", parameters: {} },
            message: "Synthetic unavailable store",
            recommendedAction: "RETRY_SAFE",
          },
        },
      }),
    ),
  ).toBe(false);
});

it("keeps unconfirmed settlement distinct from definite accepted execution", () => {
  const completed = {
    endpoint: "command.execute",
    outcome: {
      tag: "COMPLETED",
      data: {
        preparation: preparation.summary,
        attemptId: operationId,
        execution: { tag: "ACCEPTED", receipt: { ...receipt, command: "CLOSE" } },
        settlement: "CONFIRMED",
      },
    },
  } as const;
  expect(isMutationUncertain(result(completed))).toBe(false);
  expect(
    isMutationUncertain(
      result({
        ...completed,
        outcome: {
          ...completed.outcome,
          data: { ...completed.outcome.data, settlement: "UNCONFIRMED" },
        },
      }),
    ),
  ).toBe(true);
});
