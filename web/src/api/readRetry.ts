import type { ApiResult } from "../protocol/types";
import { localNotice } from "../protocol/notices";
import { responseDeadlineMs } from "./responseDeadline";

type ReadAttempt<T> = { result: ApiResult<T>; retryAfter: string | null };
const jitterMs = 250;
/** Retry-After is a lower bound; decline values that cannot fit the existing response deadline. */
const retryDelay = (header: string | null): number | null => {
  if (header === null) {
    return null;
  }
  const text = header.trim();
  const delay = /^\d+$/u.test(text) ? Number(text) * 1000 : Date.parse(text) - Date.now();
  if (!Number.isFinite(delay) || delay < 0) {
    return null;
  }
  const jitter = Math.floor(Math.random() * (jitterMs + 1));
  return delay + jitter < responseDeadlineMs ? delay + jitter : null;
};
const wait = (milliseconds: number, signal: AbortSignal): Promise<boolean> => {
  if (signal.aborted) {
    return Promise.resolve(false);
  }
  let timer: ReturnType<typeof setTimeout> | undefined;
  return new Promise<boolean>((resolve) => {
    const aborted = () => {
      resolve(false);
    };
    timer = setTimeout(() => {
      signal.removeEventListener("abort", aborted);
      resolve(true);
    }, milliseconds);
    signal.addEventListener("abort", aborted, { once: true });
  }).finally(() => {
    clearTimeout(timer);
  });
};
/** Only callers that have verified READ metadata use this one-retry path. */
export const retryRead = async <T>(
  attempt: () => Promise<ReadAttempt<T>>,
  signal: AbortSignal,
  waiting: () => () => void,
): Promise<ApiResult<T>> => {
  const first = await attempt();
  if (first.result.kind !== "hostFailure" || first.result.failure.code !== "RATE_LIMITED") {
    return first.result;
  }
  const delay = retryDelay(first.retryAfter);
  if (delay === null || signal.aborted) {
    return first.result;
  }
  const finished = waiting();
  try {
    if (!(await wait(delay, signal))) {
      return { kind: "deliveryFailure", notice: localNotice("cancelled") };
    }
    return (await attempt()).result;
  } finally {
    finished();
  }
};
