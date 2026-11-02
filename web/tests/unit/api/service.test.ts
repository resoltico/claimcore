import {
  ReadableStream as NodeReadableStream,
  type ReadableStreamDefaultController as NodeController,
} from "node:stream/web";
import { afterEach, it, expect, vi } from "vitest";
import { isMutationUncertain } from "../../../src/api/service";
import type { ApiResult, EndpointOutcome } from "../../../src/api/service";
it("explicit uncertainty is retained while definite rejection and cancellation remain definite", () => {
  const unknown: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: {
      operationId: "11111111-1111-4111-8111-111111111111",
      outcome: { kind: "OUTCOME_UNKNOWN", data: null },
    },
  };
  expect(isMutationUncertain(unknown)).toBe(true);
  expect(
    isMutationUncertain({
      kind: "outcome",
      status: 200,
      value: {
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { kind: "FAILED", data: { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" } },
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

import { service } from "../../../src/api/service";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
const operation = "10000000-0000-4000-8000-000000000001";
const installation = "90000000-0000-4000-8000-000000000001";
const receipt = {
  operationId: operation,
  position: { sequence: 1, entryHash: "a".repeat(64) },
  recordedAt: "2026-01-01T00:00:00.000000Z",
  recordedBy: operation,
  subject: {
    kind: "CASE",
    caseId: operation,
    caseReference: "CORRELATION",
    revision: 2,
    act: "CLOSE",
    snapshot: { kind: "WITHHELD" },
  },
};
const prepared = () =>
  prepareRequest("operation.execute", {
    kind: "CASE",
    operationId: operation,
    target: { kind: "REFERENCE", caseReference: "CORRELATION" },
    expectedRevision: 1,
    act: { kind: "CLOSE" },
  });
const reply = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: { "content-type": "application/json" },
  });
afterEach(() => vi.unstubAllGlobals());
it("real fetch admission checks execute envelope and receipt identities before accepting", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const matching = { operationId: operation, outcome: { kind: "ACCEPTED", data: receipt } };
  vi.mocked(fetch).mockResolvedValueOnce(reply(matching));
  expect((await service.execute(prepared(), "csrf", installation)).kind).toBe("outcome");
  const other = "90000000-0000-4000-8000-000000000099";
  for (const mismatched of [
    { ...matching, operationId: other },
    { ...matching, outcome: { kind: "ACCEPTED", data: { ...receipt, operationId: other } } },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(reply(mismatched));
    const result = await service.execute(prepared(), "csrf", installation);
    expect(result.kind).toBe("deliveryFailure");
    expect(isMutationUncertain(result)).toBe(true);
  }
});
it("resolve and observe reject a different operation envelope and matching final replies pass", async () => {
  vi.stubGlobal("fetch", vi.fn());
  for (const endpoint of ["resolve", "observe"] as const) {
    const outcome =
      endpoint === "resolve"
        ? { kind: "NOT_ACCEPTED", data: null }
        : { kind: "SUCCEEDED", data: receipt };
    vi.mocked(fetch).mockResolvedValueOnce(reply({ operationId: operation, outcome }));
    const matching =
      endpoint === "resolve"
        ? await service.resolve(operation, "csrf", installation)
        : await service.observe(operation, "csrf");
    expect(matching.kind).toBe("outcome");
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({ operationId: "90000000-0000-4000-8000-000000000099", outcome }),
    );
    const refused =
      endpoint === "resolve"
        ? await service.resolve(operation, "csrf", installation)
        : await service.observe(operation, "csrf");
    expect(refused.kind).toBe("deliveryFailure");
  }
});
it.each([
  ["ß", "SS"],
  ["ſ", "S"],
])("reference correlation refuses Unicode folding from %s to %s", async (authored, retained) => {
  vi.stubGlobal("fetch", vi.fn());
  const accepted = {
    operationId: operation,
    outcome: {
      kind: "ACCEPTED",
      data: {
        ...receipt,
        subject: {
          ...receipt.subject,
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: retained,
          revision: 2,
          act: "CLOSE",
        },
      },
    },
  };
  const request = (reference: string) =>
    prepareRequest("operation.execute", {
      kind: "CASE",
      operationId: operation,
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: 1,
      act: { kind: "CLOSE" },
    });
  vi.mocked(fetch).mockResolvedValueOnce(reply(accepted));
  expect((await service.execute(request(retained.toLowerCase()), "csrf", installation)).kind).toBe(
    "outcome",
  );
  vi.mocked(fetch).mockResolvedValueOnce(reply(accepted));
  const result = await service.execute(request(authored), "csrf", installation);
  expect(result.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(result)).toBe(true);
});

it("coordinates list summary and overview through complete response admission, not just headers", async () => {
  const { requestEndpoint } = await import("../../../src/api/service");
  const { wireReply } = await import("../../support/contract-corpus.fixtures");
  const streams: NodeController<Uint8Array>[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path) => {
    if (typeof path !== "string") {
      throw new Error("Expected a same-origin API path.");
    }
    if (path === "/api/work.overview") {
      return Promise.resolve(reply(wireReply("work.overview")));
    }
    return Promise.resolve(
      new Response(
        new NodeReadableStream<Uint8Array>({
          start(controller) {
            streams.push(controller);
          },
        }) as unknown as BodyInit,
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const list = requestEndpoint("case.list", "token", { limit: 50 });
  const summary = requestEndpoint("case.summary", "token", {});
  const overview = requestEndpoint("work.overview", "token", {});
  await vi.waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(streams).toHaveLength(2);
  streams[0]!.enqueue(new TextEncoder().encode(JSON.stringify(wireReply("case.list"))));
  streams[0]!.close();
  expect((await list).kind).toBe("outcome");
  expect((await overview).kind).toBe("outcome");
  expect(fetch).toHaveBeenCalledTimes(3);
  streams[1]!.enqueue(new TextEncoder().encode(JSON.stringify(wireReply("case.summary"))));
  streams[1]!.close();
  expect((await summary).kind).toBe("outcome");
});
