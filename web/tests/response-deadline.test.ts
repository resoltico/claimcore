import { ReadableStream } from "node:stream/web";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { withinResponseDeadline } from "../src/api/responseDeadline";
import { service, type ApiResult } from "../src/api/service";

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("fetch", vi.fn());
});
afterEach(() => {
  vi.useRealTimers();
});

it("clears the response timer on normal completion and leaves the validated result intact", async () => {
  const result: ApiResult<string> = { kind: "outcome", value: "synthetic", status: 200 };
  expect(await withinResponseDeadline(() => Promise.resolve(result))).toBe(result);
  expect(vi.getTimerCount()).toBe(0);
});

it("bounds a stalled response and ignores a later successful result", async () => {
  let complete: (result: ApiResult<string>) => void = () => undefined;
  const work = new Promise<ApiResult<string>>((resolve) => {
    complete = resolve;
  });
  const task = withinResponseDeadline(() => work);
  await vi.advanceTimersByTimeAsync(20_000);
  const result = await task;
  expect(result).toEqual({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "responseTimeout" },
  });
  complete({ kind: "outcome", value: "late", status: 200 });
  await work;
  expect(await task).toBe(result);
  expect(vi.getTimerCount()).toBe(0);
});

it("links caller cancellation and releases only the request-owned controller", async () => {
  const caller = new AbortController();
  let owned: AbortSignal | undefined;
  const pending = withinResponseDeadline(
    (signal) =>
      new Promise((resolve) => {
        owned = signal;
        signal.addEventListener("abort", () => {
          resolve({ kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } });
        });
      }),
    caller.signal,
  );
  expect(owned?.aborted).toBe(false);
  caller.abort();
  expect((await pending).kind).toBe("deliveryFailure");
  expect(owned?.aborted).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
});

it("propagates pre-cancelled callers and preserves a healthy caller on completion", async () => {
  const cancelled = new AbortController();
  cancelled.abort();
  await withinResponseDeadline((signal) => {
    expect(signal.aborted).toBe(true);
    return Promise.resolve({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
  }, cancelled.signal);
  const healthy = new AbortController();
  let owned: AbortSignal | undefined;
  await withinResponseDeadline((signal) => {
    owned = signal;
    return Promise.resolve({ kind: "outcome", value: 1, status: 200 });
  }, healthy.signal);
  expect(healthy.signal.aborted).toBe(false);
  expect(owned?.aborted).toBe(false);
  healthy.abort();
  expect(owned?.aborted).toBe(false);
  expect(vi.getTimerCount()).toBe(0);
});

it("aborts unfinished transport and clears its timer when work rejects", async () => {
  let owned: AbortSignal | undefined;
  await expect(
    withinResponseDeadline((signal) => {
      owned = signal;
      return Promise.reject(new Error("Synthetic work refusal"));
    }),
  ).rejects.toThrow("Synthetic work refusal");
  expect(owned?.aborted).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
});

it("cancels unread protocol-refused bodies without awaiting a hostile cancellation", async () => {
  const refused = vi.fn(() => Promise.reject(new Error("Synthetic cancellation refusal")));
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    new Response(new ReadableStream({ cancel: refused }) as unknown as BodyInit, {
      headers: { "Content-Type": "text/plain" },
    }),
  );
  expect((await service.context("csrf")).kind).toBe("deliveryFailure");
  expect(refused).toHaveBeenCalledOnce();
  expect(vi.getTimerCount()).toBe(0);
});

it("a caller cancelled before handoff never invokes work and is NOT_SENT", async () => {
  const signal = new AbortController();
  signal.abort();
  const work = vi.fn(() =>
    Promise.resolve({
      kind: "deliveryFailure" as const,
      notice: { kind: "local" as const, reason: "unreachable" as const },
    }),
  );
  const result = await withinResponseDeadline(work, signal.signal);
  expect(work).not.toHaveBeenCalled();
  expect(result).toMatchObject({
    kind: "deliveryFailure",
    dispatched: false,
    notice: { reason: "cancelled" },
  });
});
