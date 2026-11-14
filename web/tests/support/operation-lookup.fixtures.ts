import type { ApiResult, ServiceResponse } from "../../src/api/service";

export const absentLookup = (operationId: string) =>
  ({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      operationId,
      outcome: {
        kind: "NOT_ACCEPTED",
        document: {
          screen: { regions: [] },
          subject: { kind: "LIST" },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "UNAVAILABLE" },
          disclosure: "NONE",
        },
      },
    },
  }) as const satisfies ApiResult<ServiceResponse<"operation.get">>;

export const pendingLookup = (operationId: string, finalAt = "2026-11-07T10:10:00.000000Z") =>
  ({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      operationId,
      outcome: {
        kind: "PENDING",
        finalAt,
        document: {
          subject: { kind: "LIST" },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "UNAVAILABLE" },
          disclosure: "NONE",
          screen: {
            regions: [
              {
                role: "NOTICE",
                content: {
                  kind: "NOTICE",
                  messageId: "label.operationPending",
                  tone: "INFO",
                  reason: { kind: "INSTANT", value: finalAt },
                },
              },
            ],
          },
        },
      },
    },
  }) as const satisfies ApiResult<ServiceResponse<"operation.get">>;
