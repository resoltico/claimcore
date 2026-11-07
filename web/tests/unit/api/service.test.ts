import { timelinePage, timelineEntry } from "../../support/timeline.fixtures";
import { wireReply } from "../../support/contract-corpus.fixtures";
import { actorRefInput } from "../../support/inputs";
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
it("lookup keeps its installation binding and rejects an operation echo mismatch", async () => {
  vi.stubGlobal("fetch", vi.fn());
  for (const outcome of [
    { kind: "NOT_ACCEPTED", data: null },
    { kind: "ACCEPTED", data: { receipt, byRequester: true } },
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

it("drains dispatched obsolete reads before admitting replacements and never dispatches queued cancellations", async () => {
  const streams: NodeController<Uint8Array>[] = [];
  const deliveredSignals: AbortSignal[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    deliveredSignals.push(init!.signal!);
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
  const obsolete = new AbortController();
  const queued = new AbortController();
  const list = requestEndpoint("case.list", "token", { limit: 50 }, obsolete.signal);
  const summary = requestEndpoint("case.summary", "token", {});
  const canceled = requestEndpoint("work.overview", "token", {}, queued.signal);
  const replacement = requestEndpoint("work.overview", "token", {});
  try {
    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(2);
    });
    queued.abort();
    expect(await canceled).toMatchObject({
      kind: "deliveryFailure",
      notice: { reason: "cancelled" },
      dispatched: false,
    });
    obsolete.abort();
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(deliveredSignals[0]!.aborted).toBe(false);
    streams[0]!.enqueue(new TextEncoder().encode(JSON.stringify(wireReply("case.list"))));
    streams[0]!.close();
    expect(await list).toMatchObject({ kind: "deliveryFailure", notice: { reason: "cancelled" } });
    expect((await replacement).kind).toBe("outcome");
    expect(fetch).toHaveBeenCalledTimes(3);
    streams[1]!.enqueue(new TextEncoder().encode(JSON.stringify(wireReply("case.summary"))));
    streams[1]!.close();
    expect((await summary).kind).toBe("outcome");
  } finally {
    for (const stream of streams) {
      try {
        stream.close();
      } catch {
        /* Already drained or canceled. */
      }
    }
    await Promise.allSettled([list, summary, canceled, replacement]);
  }
});

it("bounds dispatched read draining by the independent transport deadline even when source cancellation stalls", async () => {
  vi.useFakeTimers();
  const cancel = vi.fn(
    () =>
      new Promise<void>(() => {
        /* Hostile source never settles. */
      }),
  );
  let deliveredSignal: AbortSignal | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((_path, init) => {
      deliveredSignal = init!.signal!;
      return Promise.resolve(
        new Response(new NodeReadableStream<Uint8Array>({ cancel }) as unknown as BodyInit, {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    }),
  );
  const caller = new AbortController();
  const pending = requestEndpoint("case.list", "token", { limit: 50 }, caller.signal);
  await vi.advanceTimersByTimeAsync(0);
  caller.abort();
  expect(deliveredSignal!.aborted).toBe(false);
  await vi.advanceTimersByTimeAsync(19_999);
  expect(deliveredSignal!.aborted).toBe(false);
  await vi.advanceTimersByTimeAsync(1);
  expect(await pending).toMatchObject({
    kind: "deliveryFailure",
    notice: { reason: "responseTimeout" },
  });
  expect(deliveredSignal!.aborted).toBe(true);
  expect(cancel).toHaveBeenCalledTimes(1);
});

it("admits the actual nested grant target and refuses a different actor before rendering", async () => {
  const sample = wireReply("authority.grants");
  if (sample.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected the core grant-page fixture.");
  }
  const { data } = sample.outcome;
  const { actorId } = data.actor.actor;
  const different = {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        ...data,
        actor: {
          ...data.actor,
          actor: { ...data.actor.actor, actorId: "90000000-0000-4000-8000-000000000099" },
        },
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValueOnce(reply(sample)).mockResolvedValueOnce(reply(different)),
  );
  expect((await service.authorityGrants(actorId, null, "csrf")).kind).toBe("outcome");
  const rejected = await service.authorityGrants(actorId, null, "csrf");
  expect(rejected.kind).toBe("deliveryFailure");
});
it("admits a timeline target and refuses mismatched case IDs and references", async () => {
  const data = timelinePage([timelineEntry()]);
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(reply({ outcome: { kind: "SUCCEEDED", data } }))
    .mockResolvedValueOnce(
      reply({
        outcome: {
          kind: "SUCCEEDED",
          data: { ...data, caseId: "90000000-0000-4000-8000-000000000099" },
        },
      }),
    )
    .mockResolvedValueOnce(
      reply({ outcome: { kind: "SUCCEEDED", data: { ...data, caseReference: "OTHER-CASE" } } }),
    );
  vi.stubGlobal("fetch", fetch);
  expect(
    (
      await requestEndpoint("case.timeline", "csrf", {
        target: { kind: "ID", caseId: data.caseId },
        limit: 50,
      })
    ).kind,
  ).toBe("outcome");
  expect(
    (
      await requestEndpoint("case.timeline", "csrf", {
        target: { kind: "ID", caseId: data.caseId },
        limit: 50,
      })
    ).kind,
  ).toBe("deliveryFailure");
  expect(
    (
      await requestEndpoint("case.timeline", "csrf", {
        target: { kind: "REFERENCE", caseReference: data.caseReference },
        limit: 50,
      })
    ).kind,
  ).toBe("deliveryFailure");
});
