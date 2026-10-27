// @vitest-environment node
import { expect, it } from "vitest";
import {
  loginAdmissionStatus,
  loginAdmissionAttempt,
  readLoginAdmission,
  waitForLoginPermit,
} from "../../e2e/admission-window";

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

const rateLimited = { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } };
const jsonHeaders = { "content-type": "application/json; charset=utf-8" };
it("accepts only a real redirect or a typed not-started rate refusal", () => {
  expect(loginAdmissionStatus(302, { location: "https://issuer.example/authorize" }, null)).toBe(
    302,
  );
  expect(loginAdmissionStatus(429, jsonHeaders, rateLimited)).toBe(429);
  for (const payload of [
    null,
    {},
    { hostFailure: { code: "RATE_LIMITED", phase: "STARTED_UNCONFIRMED" } },
    { hostFailure: { code: "INTERNAL", phase: "NOT_STARTED" } },
    { hostFailure: { ...rateLimited.hostFailure, extra: true } },
  ]) {
    expect(() => loginAdmissionStatus(429, jsonHeaders, payload)).toThrow(
      "E2E_LOGIN_ADMISSION_429",
    );
  }
});
it("rejects unrelated statuses, media types and a redirect with no location", () => {
  for (const status of [200, 301, 303, 307, 308, 400, 401, 403, 404, 500, 503]) {
    expect(() => loginAdmissionStatus(status, jsonHeaders, rateLimited)).toThrow(
      `E2E_LOGIN_ADMISSION_${status}`,
    );
  }
  for (const media of ["", "text/html", "text/plain", "application/jsonp"]) {
    expect(() => loginAdmissionStatus(429, { "content-type": media }, rateLimited)).toThrow(
      "E2E_LOGIN_ADMISSION_429",
    );
  }
  expect(() => loginAdmissionStatus(302, {}, null)).toThrow("E2E_LOGIN_CHALLENGE_LOCATION_MISSING");
});
it("never retries a malformed 429 as if it were a genuine limiter refusal", async () => {
  const time = clock();
  let calls = 0;
  await expect(
    waitForLoginPermit(
      () => {
        calls++;
        return Promise.resolve(
          loginAdmissionStatus(429, jsonHeaders, {
            hostFailure: { code: "RATE_LIMITED", phase: "STARTED_UNCONFIRMED" },
          }),
        );
      },
      time.pause,
      time.now,
    ),
  ).rejects.toThrow("E2E_LOGIN_ADMISSION_429");
  expect(calls).toBe(1);
  expect(time.pauses).toEqual([]);
});

it("admits a late permitted challenge before awaiting its normal provider navigation", async () => {
  const time = clock();
  let finish!: () => void;
  const navigation = new Promise<void>((resolve) => {
    finish = resolve;
  });
  const response = {
    status: () => 302,
    headers: () => ({ location: "https://issuer.example/authorize" }),
    body: () => Promise.resolve(Buffer.alloc(0)),
  };
  const controller = new AbortController();
  await waitForLoginPermit(
    async () => {
      time.advance(60_000);
      const result = await loginAdmissionAttempt(
        Promise.resolve(response),
        navigation,
        controller,
        1_000,
      );
      return result.status;
    },
    time.pause,
    time.now,
  );
  time.advance(2_000);
  finish();
  await navigation;
  expect(time.now()).toBe(62_000);
  expect(controller.signal.aborted).toBe(false);
  expect(time.pauses).toEqual([]);
});
it("propagates navigation failure and bounds an unfinished refusal body", async () => {
  const pendingResponse = Promise.withResolvers<never>().promise;
  const expired = Promise.withResolvers<never>().promise;
  await expect(
    readLoginAdmission(pendingResponse, Promise.reject(new Error("navigation")), expired),
  ).rejects.toThrow("navigation");
  let expire!: (reason: Error) => void;
  const deadline = new Promise<never>((_resolve, reject) => {
    expire = reject;
  });
  const response = {
    status: () => 429,
    headers: () => jsonHeaders,
    body: () => Promise.withResolvers<never>().promise,
  };
  const read = readLoginAdmission(Promise.resolve(response), Promise.resolve(), deadline);
  expire(new Error("deadline"));
  await expect(read).rejects.toThrow("deadline");
});
