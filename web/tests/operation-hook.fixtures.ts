import { renderHook } from "@testing-library/react";
import { vi } from "vitest";
import { useOperation } from "../src/hooks/operation/useOperation";
import type { ApiResult, ServiceResponse } from "../src/api/service";
import { current } from "./operation-form.fixtures";
import { review } from "./service-foundation.fixtures";
export const hook = () =>
  renderHook(
    ({ token, target }) =>
      useOperation({ scope: "CASE", kind: "CLOSE", current: target, token, onLockChange: vi.fn() }),
    { initialProps: { token: "token", target: current } },
  );
export const preview = (): ApiResult<ServiceResponse<"operation.preview">> => ({
  kind: "outcome",
  status: 200,
  value: {
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: { tag: "REVIEWED", data: { review } },
  },
});
export const rejected = (conflict: boolean): ApiResult<ServiceResponse<"operation.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: {
      tag: "REJECTED",
      data: conflict
        ? {
            code: "OPERATION_CONTENT_CONFLICT",
            action: "STOP_AND_INVESTIGATE",
            target: null,
            actualRevision: null,
            limits: {},
          }
        : {
            code: "ACCESS_RESOURCE_UNAVAILABLE",
            action: "NONE_REQUIRED",
            target: null,
            actualRevision: null,
            limits: {},
          },
    },
  },
});
export const pending = <T>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
