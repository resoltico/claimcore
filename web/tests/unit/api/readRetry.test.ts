import { wireResponse } from "../../support/contract-corpus.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { service, requestEndpoint } from "../../../src/api/service";
import { subscribeReadRetries } from "../../../src/api/readRetryNotice";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import { response } from "../../support/service-ui.fixtures";
const rateLimited = (retryAfter: string | null = "1") =>
  new Response(JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }), {
    status: 429,
    headers: {
      "content-type": "application/json",
      ...(retryAfter === null ? {} : { "Retry-After": retryAfter }),
    },
  });
const success = () =>
  response("case.list", "SUCCEEDED", { items: [], offers: [], nextCursor: null });
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
it("honors Retry-After once with bounded positive jitter and preserves the exact read request", async () => {
  vi.useFakeTimers();
  vi.spyOn(Math, "random").mockReturnValue(0.5);
  const times: number[] = [];
  const fetch = vi.fn().mockImplementation(() => {
    times.push(Date.now());
    return Promise.resolve(times.length === 1 ? rateLimited() : success());
  });
  vi.stubGlobal("fetch", fetch);
  const notice = vi.fn();
  const close = subscribeReadRetries("csrf", notice);
  try {
    const read = service.list(null, 10, "csrf");
    await vi.waitFor(() => {
      expect(notice).toHaveBeenCalledWith(true);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1250);
    expect((await read).kind).toBe("outcome");
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(times[1]! - times[0]!).toBeGreaterThanOrEqual(1000);
    expect(times[1]! - times[0]!).toBeLessThanOrEqual(1250);
    expect(fetch.mock.calls[1]).toEqual(fetch.mock.calls[0]);
    expect(notice).toHaveBeenLastCalledWith(false);
  } finally {
    close();
  }
});
it("stops after the second 429 rather than starting a retry loop", async () => {
  vi.useFakeTimers();
  vi.spyOn(Math, "random").mockReturnValue(0);
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(rateLimited()));
  vi.stubGlobal("fetch", fetch);
  const read = service.list(null, 10, "csrf");
  await vi.advanceTimersByTimeAsync(3000);
  expect(await read).toMatchObject({ kind: "hostFailure", failure: { code: "RATE_LIMITED" } });
  await vi.advanceTimersByTimeAsync(30_000);
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("cancels during the wait without dispatching a second read", async () => {
  vi.useFakeTimers();
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(rateLimited()));
  vi.stubGlobal("fetch", fetch);
  const notice = vi.fn();
  const close = subscribeReadRetries("csrf", notice);
  const controller = new AbortController();
  try {
    const read = service.list(null, 10, "csrf", controller.signal);
    await vi.waitFor(() => {
      expect(notice).toHaveBeenCalledWith(true);
    });
    controller.abort();
    await vi.advanceTimersByTimeAsync(5000);
    expect(await read).toMatchObject({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "cancelled" },
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(notice).toHaveBeenLastCalledWith(false);
  } finally {
    close();
  }
});
it.each([null, "invalid", "999999999999999999999999", "-1"])(
  "does not guess a delay for unusable Retry-After %s",
  async (header) => {
    const fetch = vi.fn().mockImplementation(() => Promise.resolve(rateLimited(header)));
    vi.stubGlobal("fetch", fetch);
    expect(await service.list(null, 10, "csrf")).toMatchObject({
      kind: "hostFailure",
      failure: { code: "RATE_LIMITED" },
    });
    expect(fetch).toHaveBeenCalledOnce();
  },
);
it("accepts an HTTP-date lower bound without retrying before it", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-11-04T12:00:00Z"));
  vi.spyOn(Math, "random").mockReturnValue(0);
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(rateLimited("Wed, 04 Nov 2026 12:00:01 GMT"))
    .mockResolvedValueOnce(success());
  vi.stubGlobal("fetch", fetch);
  const read = service.list(null, 10, "csrf");
  await vi.advanceTimersByTimeAsync(999);
  expect(fetch).toHaveBeenCalledTimes(1);
  await vi.advanceTimersByTimeAsync(1);
  expect((await read).kind).toBe("outcome");
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("retains response correlation checks on the retried read", async () => {
  vi.useFakeTimers();
  vi.spyOn(Math, "random").mockReturnValue(0);
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(rateLimited())
    .mockResolvedValueOnce(wireResponse("case.get"));
  vi.stubGlobal("fetch", fetch);
  const read = service.get("DIFFERENT-REFERENCE", "csrf");
  await vi.advanceTimersByTimeAsync(1250);
  expect(await read).toMatchObject({
    kind: "deliveryFailure",
    notice: { kind: "invalidHttp", status: 200 },
  });
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("does not retry a malformed 429 host response", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValue(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "STARTED_UNCONFIRMED" } }),
        { status: 429, headers: { "content-type": "application/json", "Retry-After": "1" } },
      ),
    );
  vi.stubGlobal("fetch", fetch);
  expect(await service.list(null, 10, "csrf")).toMatchObject({ kind: "deliveryFailure" });
  expect(fetch).toHaveBeenCalledOnce();
});
const prepared = prepareRequest("operation.execute", {
  kind: "CASE",
  operationId: "10000000-0000-4000-8000-000000000001",
  target: { kind: "REFERENCE", caseReference: "RETRY" },
  expectedRevision: 1,
  act: { kind: "CLOSE" },
});
const installation = "90000000-0000-4000-8000-000000000001";
it.each([
  { name: "ledger execution", send: () => service.execute(prepared, "csrf", installation) },
  {
    name: "access request dismissal",
    send: () =>
      requestEndpoint("authority.requests.dismiss", "csrf", {
        kind: "HUMAN",
        issuer: "https://issuer.example/",
        subject: "waiting",
      }),
  },
  { name: "logout", send: () => service.logout("csrf") },
])("never retries $name on 429", async ({ send }) => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(rateLimited()));
  vi.stubGlobal("fetch", fetch);
  const notice = vi.fn();
  const close = subscribeReadRetries("csrf", notice);
  try {
    expect(await send()).toMatchObject({ kind: "hostFailure", failure: { code: "RATE_LIMITED" } });
    expect(fetch).toHaveBeenCalledOnce();
    expect(notice).not.toHaveBeenCalled();
  } finally {
    close();
  }
});
