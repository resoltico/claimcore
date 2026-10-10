import type { ApiResult } from "./types";
import { localNotice } from "./notices";

const responseDeadlineMs = 20_000;

/** Transport cancellation bounds consumption; a dispatched mutation remains uncertain. */
export const withinResponseDeadline = async <T>(
  work: (signal: AbortSignal) => Promise<ApiResult<T>>,
  callerSignal?: AbortSignal,
): Promise<ApiResult<T>> => {
  const controller = new AbortController();
  const cancel = () => {
    controller.abort();
  };
  callerSignal?.addEventListener("abort", cancel, { once: true });
  if (callerSignal?.aborted === true) {
    cancel();
  }
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<ApiResult<T>>((resolve) => {
    timer = setTimeout(() => {
      resolve({ kind: "deliveryFailure", notice: localNotice("responseTimeout") });
      controller.abort();
    }, responseDeadlineMs);
  });
  try {
    return await Promise.race([work(controller.signal), expired]);
  } finally {
    clearTimeout(timer);
    callerSignal?.removeEventListener("abort", cancel);
    controller.abort();
  }
};
