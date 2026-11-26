import { afterEach, expect, it, vi } from "vitest";
import { service } from "../../../src/api/service";
import { recoveryLookup } from "../../../src/recovery/recoveryLookup";
import { absentLookup, pendingLookup } from "../../support/operation-lookup.fixtures";
import { recoveryRecord } from "../../support/recovery.fixtures";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  localStorage.clear();
});

it("shares the read, checks at the server deadline, and waits through Retry-After", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-11-07T10:09:59.750Z"));
  vi.spyOn(Math, "random").mockReturnValue(0.5);

  const record = recoveryRecord();
  const pending = pendingLookup(record.operationId);
  const absent = absentLookup(record.operationId);
  const unavailable = {
    kind: "hostFailure",
    status: 429,
    failure: { code: "RATE_LIMITED", phase: "NOT_STARTED" },
    retryAfterMs: 5000,
  } as const;
  const get = vi
    .spyOn(service, "operationGet")
    .mockResolvedValueOnce(pending)
    .mockResolvedValueOnce(unavailable)
    .mockResolvedValueOnce(absent);
  const execute = vi.spyOn(service, "execute");
  const first = new AbortController();
  const second = new AbortController();

  const editor = recoveryLookup(record, "session", 2, first.signal);
  const workspace = recoveryLookup(record, "session", 2, second.signal);
  first.abort();
  await expect(editor).resolves.toMatchObject({ kind: "deliveryFailure" });
  await expect(workspace).resolves.toEqual(pending);
  expect(get).toHaveBeenCalledTimes(1);
  expect(get.mock.calls[0]?.[3]?.aborted).toBe(false);

  const deadline = recoveryLookup(record, "session", 2, second.signal);
  await vi.advanceTimersByTimeAsync(249);
  expect(get).toHaveBeenCalledTimes(1);
  await vi.advanceTimersByTimeAsync(1);
  await expect(deadline).resolves.toEqual(unavailable);

  const final = recoveryLookup(record, "session", 2, second.signal);
  await vi.advanceTimersByTimeAsync(4999);
  expect(get).toHaveBeenCalledTimes(2);
  await vi.advanceTimersByTimeAsync(1);
  await expect(final).resolves.toEqual(absent);
  expect(get).toHaveBeenCalledTimes(3);
  expect(execute).not.toHaveBeenCalled();
});
