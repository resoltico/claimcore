import type { PreparedRequest } from "../../src/protocol/preparedRequest";
import { recoveryOptions } from "./recovery.fixtures";
import { renderHook } from "@testing-library/react";
import { vi } from "vitest";
import { useOperation } from "../../src/hooks/operation/useOperation";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { current } from "./operation-form.fixtures";
import { review } from "./service-foundation.fixtures";
export const hook = () =>
  renderHook(
    ({ credential, target }) =>
      useOperation({
        ...recoveryOptions,
        subject: "CASE",
        kind: "CLOSE",
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
const previewIdentity = (input: PreparedRequest<"operation.execute">["input"] | undefined) => ({
  reference:
    input !== undefined && input.kind !== "AUTHORITY" && input.target.kind === "REFERENCE"
      ? input.target.caseReference
      : current.caseReference,
  expected: input?.kind === "CASE" ? input.expectedRevision : current.revision,
  caseId:
    input?.kind === "CASE" && input.target.kind === "ID" ? input.target.caseId : current.caseId,
});
export const preview = (
  prepared?: PreparedRequest<"operation.execute">,
): ApiResult<ServiceResponse<"operation.preview">> => {
  const input = prepared?.input;
  const { reference, expected, caseId } = previewIdentity(input);
  if (review.subject.kind !== "CASE") {
    throw new Error("Expected independently authored case review.");
  }
  const after = {
    ...review.subject.after,
    caseId,
    caseReference: reference,
    revision: expected + 1,
    state:
      review.subject.after.state.kind === "ERASED"
        ? review.subject.after.state
        : {
            ...review.subject.after.state,
            fields: { ...review.subject.after.state.fields, caseReference: reference },
          },
  };
  const ownerReview = {
    ...review,
    subject: {
      ...review.subject,
      before:
        input?.kind === "CASE" && input.act.kind === "OPEN"
          ? null
          : { ...after, revision: expected },
      after:
        input?.kind === "CASE" && input.act.kind === "OPEN"
          ? { ...after, caseId: input.operationId }
          : after,
    },
  };
  return {
    kind: "outcome",
    status: 200,
    value: {
      operationId: input?.operationId ?? "10000000-0000-4000-8000-000000000001",
      outcome: {
        kind: "REVIEWED",
        data: {
          review: ownerReview,
        },
      },
    },
  };
};
export const rejected = (conflict: boolean): ApiResult<ServiceResponse<"operation.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: {
      kind: "REJECTED",
      data: conflict
        ? {
            code: "IDEMPOTENCY_CONFLICT",
            action: "STOP_AND_INVESTIGATE",
          }
        : {
            code: "RESOURCE_UNAVAILABLE",
            action: "ASK_FOR_ACCESS",
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
