import type { ApiResult } from "./types";
import { localNotice } from "./notices";

const responseDeadlineMs = 20_000;

/** Bounds response knowledge without aborting an already-dispatched mutation. */
export const withinResponseDeadline = async <T>(
  work: Promise<ApiResult<T>>,
): Promise<ApiResult<T>> => {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<ApiResult<T>>((resolve) => {
    timer = setTimeout(() => {
      resolve({ kind: "deliveryFailure", notice: localNotice("responseTimeout") });
    }, responseDeadlineMs);
  });
  try {
    return await Promise.race([work, expired]);
  } finally {
    clearTimeout(timer);
  }
};
