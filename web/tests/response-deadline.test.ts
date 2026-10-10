import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { withinResponseDeadline } from "../src/api/responseDeadline";
import { isMutationUncertain, v3, type ApiResult } from "../src/api/v3";
import { createDraft } from "../src/domain/metadata";
import { operationId } from "./v3-ui.fixtures";

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

it("times out submission and witnessed export with transport abort while preserving mutation uncertainty", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockImplementation(
    () =>
      new Promise<Response>(() => {
        /* Synthetic stalled transport. */
      }),
  );
  const draft = createDraft(operationId, "SYNTHETIC", "1", "CLOSE", {});
  const submission = v3.submit(draft, "token");
  const exporting = v3.recoveryExport(operationId, "a".repeat(64), "token");
  await vi.advanceTimersByTimeAsync(20_000);
  expect(isMutationUncertain(await submission)).toBe(true);
  expect(await exporting).toMatchObject({
    kind: "deliveryFailure",
    notice: { reason: "responseTimeout" },
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  for (const [, init] of fetch.mock.calls) {
    expect(init?.signal?.aborted).toBe(true);
  }
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
  await withinResponseDeadline(
    () => Promise.resolve({ kind: "outcome", value: 1, status: 200 }),
    healthy.signal,
  );
  expect(healthy.signal.aborted).toBe(false);
  expect(vi.getTimerCount()).toBe(0);
});
