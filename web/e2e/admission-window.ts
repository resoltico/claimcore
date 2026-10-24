import type { Response } from "@playwright/test";
import { strictJsonBytes } from "../src/api/responseBytes";
import { isHostFailure } from "../src/protocol/client";
const loginAdmissionWindow = 61_000;
export const loginNavigationTimeout = 30_000;
export const freshLoginWorkflowTimeout = loginAdmissionWindow + loginNavigationTimeout;
export const loginAdmissionStatus = (
  status: number,
  headers: Readonly<Record<string, string>>,
  payload: unknown,
): 302 | 429 => {
  if (status === 302) {
    const location = headers["location"]?.trim();
    if (location === undefined || location === "") {
      throw new Error("E2E_LOGIN_CHALLENGE_LOCATION_MISSING");
    }
    return 302;
  }
  const media = headers["content-type"]?.split(";", 1)[0]?.trim().toLowerCase();
  if (
    status === 429 &&
    media === "application/json" &&
    isHostFailure(payload, status) &&
    payload.hostFailure.code === "RATE_LIMITED" &&
    payload.hostFailure.phase === "NOT_STARTED"
  ) {
    return 429;
  }
  throw new Error(`E2E_LOGIN_ADMISSION_${status}`);
};
export const readLoginAdmission = async (
  response: Promise<Pick<Response, "status" | "headers" | "body">>,
  navigation: Promise<unknown>,
  expired: Promise<never>,
): Promise<302 | 429> => {
  const admission = await Promise.race([response, navigation.then(() => response), expired]);
  const payload: unknown =
    admission.status() === 429
      ? strictJsonBytes(await Promise.race([admission.body(), expired]))
      : null;
  return loginAdmissionStatus(admission.status(), admission.headers(), payload);
};
export const loginAdmissionAttempt = async (
  response: Promise<Pick<Response, "status" | "headers" | "body">>,
  navigation: Promise<unknown>,
  controller: AbortController,
  remaining: number,
) => {
  const timer = setTimeout(() => {
    controller.abort();
  }, remaining);
  const expired = new Promise<never>((_resolve, reject) => {
    controller.signal.addEventListener(
      "abort",
      () => {
        reject(new Error("E2E_LOGIN_ADMISSION_TIMEOUT"));
      },
      { once: true },
    );
  });
  let admitted = false;
  try {
    const status = await readLoginAdmission(response, navigation, expired);
    admitted = status === 302;
    if (!admitted) {
      await Promise.race([navigation, expired]);
    }
    return { status, navigation };
  } finally {
    clearTimeout(timer);
    if (!admitted) {
      controller.abort();
    }
  }
};
export const waitForLoginPermit = async (
  probe: (remainingMilliseconds: number) => Promise<number>,
  pause: (milliseconds: number) => Promise<void>,
  now: () => number = () => performance.now(),
): Promise<void> => {
  const deadline = now() + loginAdmissionWindow;
  while (true) {
    const budget = deadline - now();
    if (budget <= 0) {
      break;
    }
    const status = await probe(budget);
    if (now() >= deadline) {
      break;
    }
    if (status === 302) {
      return;
    }
    if (status !== 429) {
      throw new Error(`E2E_RATE_RECOVERY_STATUS_${status}`);
    }
    const remaining = deadline - now();
    if (remaining > 0) {
      await pause(Math.min(1_000, remaining));
    }
  }
  throw new Error("E2E_RATE_RECOVERY_TIMEOUT");
};
