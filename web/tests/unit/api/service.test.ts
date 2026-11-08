import { wireReply } from "../../support/reply.fixtures";
import { actorRefInput } from "../../support/inputs";
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

import { service, requestEndpoint } from "../../../src/api/service";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
const operation = "10000000-0000-7000-8000-000000000001";
const installation = "90000000-0000-4000-8000-000000000001";
const receipt = {
  basis: {
    observedAt: "2026-11-04T12:00:00.000000Z",
    businessDate: "2026-11-04",
    timeZone: "Etc/UTC",
  },
  operationId: operation,
  position: { sequence: 1, entryHash: "a".repeat(64) },
  recordedAt: "2026-01-01T00:00:00.000000Z",
  recordedBy: actorRefInput({ actorId: operation }),
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
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});
it("checks the typed execute operation echo before accepting delivery", async () => {
  const matching = {
    operationId: operation,
    outcome: { view: { kind: "RECORD" as const, sections: [] }, kind: "ACCEPTED", data: receipt },
  };
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(reply(matching)));
  expect((await service.execute(prepared(), "csrf", installation)).kind).toBe("outcome");
  vi.mocked(fetch).mockResolvedValueOnce(
    reply({ ...matching, operationId: "90000000-0000-7000-8000-000000000099" }),
  );
  const result = await service.execute(prepared(), "csrf", installation);
  expect(result.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(result)).toBe(true);
});
it("lookup keeps its installation binding and rejects an operation echo mismatch", async () => {
  vi.stubGlobal("fetch", vi.fn());
  for (const outcome of [
    { kind: "NOT_ACCEPTED", data: null },
    {
      view: { kind: "RECORD" as const, sections: [] },
      kind: "ACCEPTED",
      data: { receipt, byRequester: true },
    },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(reply({ operationId: operation, outcome }));
    const matching = await service.operationGet(operation, "csrf", installation);
    expect(matching.kind).toBe("outcome");
    expect(vi.mocked(fetch).mock.calls.at(-1)?.[1]?.headers).toMatchObject({
      "ClaimCore-Installation": installation,
    });
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({ operationId: "90000000-0000-4000-8000-000000000099", outcome }),
    );
    expect((await service.operationGet(operation, "csrf", installation)).kind).toBe(
      "deliveryFailure",
    );
  }
});
it("admits structurally valid server reads without reconstructing business correlation", async () => {
  const sample = wireReply("case.get");
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(reply(sample)));
  expect((await service.get("AUTHORED-REFERENCE", "csrf")).kind).toBe("outcome");
});

it("dispatches independent reads immediately and cancels an obsolete fetch", async () => {
  const signals: AbortSignal[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((_path, init) => {
      signals.push(init!.signal!);
      return new Promise<Response>(() => {
        /* A fetch that ignores cancellation must not retain its caller. */
      });
    }),
  );
  const caller = new AbortController();
  const list = requestEndpoint("case.list", "csrf", { limit: 50 }, caller.signal);
  const second = new AbortController();
  const summary = requestEndpoint("case.summary", "csrf", {}, second.signal);
  const third = new AbortController();
  const views = requestEndpoint("work.views", "csrf", {}, third.signal);
  expect(fetch).toHaveBeenCalledTimes(3);
  caller.abort();
  expect(signals[0]!.aborted).toBe(true);
  expect(await list).toMatchObject({ kind: "deliveryFailure", notice: { reason: "cancelled" } });
  second.abort();
  third.abort();
  await Promise.all([summary, views]);
});

it("reports a busy read once without an automatic retry", async () => {
  const busy = new Response(
    JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
    {
      status: 429,
      headers: { "content-type": "application/json", "Retry-After": "1" },
    },
  );
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(busy));
  const result = await requestEndpoint("case.list", "csrf", { limit: 50 });
  expect(result).toMatchObject({
    kind: "hostFailure",
    status: 429,
    failure: { code: "RATE_LIMITED" },
  });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("retains the independent response deadline for an unresponsive fetch", async () => {
  vi.useFakeTimers();
  let delivered: AbortSignal | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((_path, init) => {
      delivered = init!.signal!;
      return new Promise<Response>(() => {
        /* Network never replies. */
      });
    }),
  );
  const pending = requestEndpoint("case.list", "csrf", { limit: 50 });
  await vi.advanceTimersByTimeAsync(20_000);
  expect(await pending).toMatchObject({
    kind: "deliveryFailure",
    notice: { reason: "responseTimeout" },
  });
  expect(delivered!.aborted).toBe(true);
});
