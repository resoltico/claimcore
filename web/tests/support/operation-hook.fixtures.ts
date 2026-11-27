import { operationAffordance } from "./affordances.fixtures";
import type { PreparedRequest } from "../../src/protocol/preparedRequest";
import { recoveryOptions } from "./recovery.fixtures";
import { renderHook } from "@testing-library/react";
import { vi } from "vitest";
import { useOperation } from "../../src/operation/useOperation";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { current, preparedFor } from "./operation-form.fixtures";
import { closeReview, openReview } from "./operation-documents.fixtures";
export const hook = () =>
  renderHook(
    ({ credential, target }) =>
      useOperation({
        ...recoveryOptions,
        subject: "CASE",
        kind: "case.close",
        current: target,
        credential,
        onLockChange: vi.fn(),
      }),
    {
      initialProps: {
        credential: "token",
        target: current,
      },
    },
  );
export const preview = (
  prepared: PreparedRequest<"operation.execute"> = preparedFor(),
): ApiResult<ServiceResponse<"operation.preview">> => {
  const { input } = prepared;
  if (
    input.binding.kind !== "CASE" ||
    (input.task !== "case.open" && input.task !== "case.close")
  ) {
    throw new Error("This fixture reviews an authored OPEN or CLOSE command.");
  }
  const source = input.task === "case.open" ? openReview : closeReview;
  const confirmation =
    input.task === "case.open"
      ? { kind: "OPERATION" as const, operationId: input.operationId }
      : {
          kind: "CASE_REFERENCE" as const,
          reference:
            input.binding.target.kind === "REFERENCE"
              ? input.binding.target.caseReference
              : "CC-3PBY-1JRP-Z70W-2VXH-71",
        };
  return {
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      operationId: input.operationId,
      outcome: {
        kind: "REVIEWED",
        review: {
          ...source,
          document: {
            ...source.document,
            subject: {
              kind: "REVIEW",
              consequence: "ROUTINE",
              confirmation,
            },
            actions: [
              operationAffordance(
                input.task,
                input.binding,
                Object.fromEntries(
                  Object.entries(input.inputs).filter(
                    (entry): entry is [string, string | null] =>
                      entry[1] === null ||
                      typeof entry[1] === "string" ||
                      typeof entry[1] === "number" ||
                      typeof entry[1] === "boolean",
                  ),
                ),
                [],
              ),
            ],
          },
        },
      },
    },
  };
};
export const rejected = (conflict: boolean): ApiResult<ServiceResponse<"operation.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    representation: "HUMAN",
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: {
            messageId: conflict
              ? "diagnostic.IDEMPOTENCY_CONFLICT"
              : "diagnostic.RESOURCE_UNAVAILABLE",
            arguments: [],
          },
          action: conflict ? "STOP_AND_INVESTIGATE" : "ASK_FOR_ACCESS",
        },
      },
    },
  },
});
export const pending = <T>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return {
    promise,
    finish,
  };
};
