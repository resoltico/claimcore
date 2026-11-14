import { operationAffordance } from "../../support/affordances.fixtures";
import type { ResponseFor } from "@contracts/client";
import { afterEach, expect, it, vi } from "vitest";
import { requestEndpoint } from "../../../src/api/service";
import { EndpointCache, endpointCacheKey, typedResult } from "../../../src/hooks/endpointCache";
const human = {
  representation: "HUMAN",
  outcome: {
    kind: "REJECTED",
    rejection: {
      kind: "REFUSED",
      advice: {
        action: "ASK_FOR_ACCESS",
        message: {
          messageId: "diagnostic.RESOURCE_UNAVAILABLE",
          arguments: [],
        },
      },
    },
  },
};
const data = {
  representation: "DATA",
  outcome: {
    kind: "REJECTED",
    rejection: {
      code: "RESOURCE_UNAVAILABLE",
      action: "ASK_FOR_ACCESS",
    },
  },
};
const input = {
  target: {
    kind: "REFERENCE",
    caseReference: "SYNTHETIC-CASE",
  },
} as const;
const response = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "Content-Type": "application/json",
    },
  });
afterEach(() => vi.unstubAllGlobals());
it("cached observations are admitted for the requested channel before gaining a browser response type", () => {
  const caseGetReply = data as ResponseFor<"case.get", "DATA">;
  if (caseGetReply.representation !== "DATA") {
    throw new Error(
      "A structurally valid machine observation is required for the negative control.",
    );
  }
  const result = {
    kind: "outcome" as const,
    status: 200,
    value: caseGetReply,
  };
  expect(typedResult("case.get", result)?.kind).toBe("deliveryFailure");
  expect(typedResult("case.get", result, "DATA")?.kind).toBe("outcome");
});
it("sends transport selection explicitly and rejects a structurally valid reply of the other representation", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(response(data));
  vi.stubGlobal("fetch", fetch);
  const result = await requestEndpoint("case.get", "csrf", input, undefined, undefined, "HUMAN");
  expect(result.kind).toBe("deliveryFailure");
  expect(fetch).toHaveBeenCalledOnce();
  expect(fetch.mock.calls[0]?.[1]?.headers).toMatchObject({
    "ClaimCore-Representation": "HUMAN",
  });
  expect(fetch.mock.calls[0]?.[1]?.body).toBe(JSON.stringify(input));
});
it("keeps concurrent HUMAN and DATA observations distinct through the actual request cache", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) => {
    const headers = new Headers(options?.headers);
    return Promise.resolve(
      response(headers.get("ClaimCore-Representation") === "HUMAN" ? human : data),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const cache = new EndpointCache();
  const humanKey = endpointCacheKey("owner-installation-actor-epoch", "case.get", input, "HUMAN");
  const dataKey = endpointCacheKey("owner-installation-actor-epoch", "case.get", input, "DATA");
  const observe = (representation: "HUMAN" | "DATA", key: string) =>
    cache.fetch(key, (signal) =>
      requestEndpoint("case.get", "csrf", input, signal, undefined, representation),
    );
  const first = observe("HUMAN", humanKey);
  const same = observe("HUMAN", humanKey);
  const separate = observe("DATA", dataKey);
  expect(first).toBe(same);
  expect(first).not.toBe(separate);
  const [humanResult, dataResult] = await Promise.all([first, separate]);
  expect(humanResult.kind).toBe("outcome");
  expect(dataResult.kind).toBe("outcome");
  if (humanResult.kind !== "outcome" || dataResult.kind !== "outcome") {
    throw new Error("Expected both separately admitted observations.");
  }
  expect(humanResult.value).toEqual(human);
  expect(dataResult.value).toEqual(data);
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls.map((call) => call[1]?.body)).toEqual([
    JSON.stringify(input),
    JSON.stringify(input),
  ]);
});
const operationInput = {
  operationId: "019aa6a0-0000-7000-8000-000000000001",
  task: "case.close",
  binding: {
    kind: "CASE",
    target: {
      kind: "REFERENCE",
      caseReference: "SYNTHETIC-CASE",
    },
    expectedRevision: 1,
  },
  inputs: {},
} as const;
const operationReview = {
  representation: "HUMAN",
  operationId: operationInput.operationId,
  outcome: {
    kind: "REVIEWED",
    review: {
      document: {
        screen: {
          regions: [],
        },
        subject: {
          kind: "REVIEW",
          confirmation: {
            kind: "CASE_REFERENCE",
            reference: "SYNTHETIC-CASE",
          },
          consequence: "ROUTINE",
        },
        actions: [
          operationAffordance(
            "case.close",
            {
              kind: "CASE",
              target: { kind: "ID", caseId: "019aa6a0-0000-7000-8000-000000000002" },
              expectedRevision: 1,
            },
            {},
            [],
          ),
        ],
        links: [],
        history: null,
        nextCursor: null,
        basis: {
          kind: "SNAPSHOT",
          observation: {
            observedAt: "2026-11-09T12:00:00.000000Z",
            businessDate: "2026-11-09",
            timeZone: "Etc/UTC",
          },
        },
        disclosure: "CURRENT",
      },
      presentation: {
        changes: [],
        unchanged: [],
      },
    },
  },
};
it("sends the same prepared operation bytes for HUMAN review and execution", async () => {
  const { prepareRequest } = await import("../../../src/protocol/preparedRequest");
  const { service } = await import("../../../src/api/service");
  const prepared = prepareRequest("operation.execute", operationInput);
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response(operationReview))
    .mockResolvedValueOnce(
      response({
        representation: "HUMAN",
        operationId: operationInput.operationId,
        outcome: {
          kind: "OUTCOME_UNKNOWN",
        },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  const preview = await service.preview(prepared, "csrf");
  expect(preview.kind).toBe("outcome");
  const executed = await service.execute(prepared, "csrf", "90000000-0000-4000-8000-000000000001");
  expect(executed.kind).toBe("outcome");
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls.map((call) => call[1]?.body)).toEqual([prepared.bytes, prepared.bytes]);
  for (const call of fetch.mock.calls) {
    expect(call[1]?.headers).toMatchObject({
      "ClaimCore-Representation": "HUMAN",
    });
  }
  expect(prepared.input.operationId).toBe(operationInput.operationId);
});
it("refuses a valid DATA operation response or wrong operation echo without dispatching a replacement", async () => {
  const { prepareRequest } = await import("../../../src/protocol/preparedRequest");
  const { service } = await import("../../../src/api/service");
  const prepared = prepareRequest("operation.execute", operationInput);
  for (const value of [
    {
      representation: "DATA",
      operationId: operationInput.operationId,
      outcome: {
        kind: "OUTCOME_UNKNOWN",
        data: null,
      },
    },
    {
      representation: "HUMAN",
      operationId: "019aa6a0-0000-7000-8000-000000000009",
      outcome: {
        kind: "OUTCOME_UNKNOWN",
      },
    },
  ]) {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(response(value));
    vi.stubGlobal("fetch", fetch);
    const result = await service.execute(prepared, "csrf", "90000000-0000-4000-8000-000000000001");
    expect(result.kind).toBe("deliveryFailure");
    expect(fetch).toHaveBeenCalledOnce();
    expect(fetch.mock.calls[0]?.[1]?.body).toBe(prepared.bytes);
  }
});
