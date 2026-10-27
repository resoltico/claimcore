import { vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { wireReply } from "./contract-corpus.fixtures";
export const authorityPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REVIEWED",
          data: {
            review: {
              subject: {
                kind: "AUTHORITY",
                target: null,
                before: {
                  enabled: false,
                  grants: [],
                },
                after: {
                  enabled: true,
                  grants: [],
                },
              },
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
    value.outcome.kind !== "ACCEPTED" ||
    value.outcome.data.subject.kind !== "AUTHORITY" ||
    prepared.input.kind !== "AUTHORITY"
  ) {
    throw new Error("Authority fixture required.");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      operationId: prepared.input.operationId,
      outcome: {
        kind: "ACCEPTED",
        data: {
          ...value.outcome.data,
          operationId: prepared.input.operationId,
          subject: {
            ...value.outcome.data.subject,
            act: prepared.input.act.kind,
          },
        },
      },
    },
  };
};
