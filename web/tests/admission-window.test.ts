// @vitest-environment node
import { expect, it } from "vitest";
import { waitForLoginPermit } from "../e2e/admission-window";

const clock = () => {
  let elapsed = 0;
  const pauses: number[] = [];
  return {
    now: () => elapsed,
    pauses,
    pause: (milliseconds: number) => {
      pauses.push(milliseconds);
      elapsed += milliseconds;
      return Promise.resolve();
    },
    advance: (milliseconds: number) => {
      elapsed += milliseconds;
    },
  };
};

it("continues as soon as the real fixed window admits a request", async () => {
  const time = clock();
  let calls = 0;
  await waitForLoginPermit(() => Promise.resolve(++calls === 3 ? 302 : 429), time.pause, time.now);
  expect(calls).toBe(3);
  expect(time.pauses).toEqual([1_000, 1_000]);
});

it("refuses an unrecovered limiter after 61 seconds", async () => {
  const time = clock();
  let calls = 0;
  await expect(
    waitForLoginPermit(
      () => {
        calls += 1;
        return Promise.resolve(429);
      },
      time.pause,
      time.now,
    ),
  ).rejects.toThrow("E2E_RATE_RECOVERY_TIMEOUT");
  expect(calls).toBe(61);
  expect(time.now()).toBe(61_000);
});

it("counts response latency against the bound and caps its final pause", async () => {
  const time = clock();
  await expect(
    waitForLoginPermit(
      () => {
        time.advance(60_500);
        return Promise.resolve(429);
      },
      time.pause,
      time.now,
    ),
  ).rejects.toThrow("E2E_RATE_RECOVERY_TIMEOUT");
  expect(time.pauses).toEqual([500]);
});

it("rejects a success delivered after its deadline", async () => {
  const time = clock();
  await expect(
    waitForLoginPermit(
      (remaining) => {
        expect(remaining).toBe(61_000);
        time.advance(61_001);
        return Promise.resolve(302);
      },
      time.pause,
      time.now,
    ),
  ).rejects.toThrow("E2E_RATE_RECOVERY_TIMEOUT");
  expect(time.pauses).toEqual([]);
});

it("fails immediately on unexpected status or transport failure", async () => {
  for (const status of [200, 401, 403, 500, 503]) {
    const time = clock();
    await expect(
      waitForLoginPermit(() => Promise.resolve(status), time.pause, time.now),
    ).rejects.toThrow(`E2E_RATE_RECOVERY_STATUS_${status}`);
    expect(time.pauses).toEqual([]);
  }
  const time = clock();
  await expect(
    waitForLoginPermit(() => Promise.reject(new Error("transport")), time.pause, time.now),
  ).rejects.toThrow("transport");
  expect(time.pauses).toEqual([]);
});

it("never gives the HTTP client a zero or negative timeout at the deadline", async () => {
  const readings = [0, 60_999, 61_000];
  let calls = 0;
  await expect(
    waitForLoginPermit(
      (remaining) => {
        calls += 1;
        expect(remaining).toBe(1);
        return Promise.resolve(429);
      },
      () => Promise.resolve(),
      () => readings.shift() ?? 61_000,
    ),
  ).rejects.toThrow("E2E_RATE_RECOVERY_TIMEOUT");
  expect(calls).toBe(1);
});
