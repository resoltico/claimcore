import { vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { wireReply } from "./contract-corpus.fixtures";
export const authorityPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        recoveryId: null,
        operationId: prepared.input.operationId,
        outcome: {
          tag: "REVIEWED",
          data: {
            review: {
              scope: "AUTHORITY",
              target: null,
              before: { enabled: false, grants: [] },
              after: { enabled: true, grants: [] },
            },
          },
        },
      },
    }),
  );
export const authorityAcceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  const value = wireReply("operation.execute", "AUTHORITY");
  if (
    value.outcome.tag !== "ACCEPTED" ||
    value.outcome.data.scope.kind !== "AUTHORITY" ||
    prepared.input.scope !== "AUTHORITY"
  ) {
    throw new Error("Authority fixture required.");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      recoveryId: null,
      operationId: prepared.input.operationId,
      outcome: {
        tag: "ACCEPTED",
        data: {
          ...value.outcome.data,
          operationId: prepared.input.operationId,
          scope: { ...value.outcome.data.scope, act: prepared.input.act.kind },
        },
      },
    },
  };
};
