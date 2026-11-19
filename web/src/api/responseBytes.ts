/** Counts received bytes before retention; cancellation never waits on a hostile source. */
export const responseBytes = async (
  response: Response,
  maximum: number,
  signal: AbortSignal,
): Promise<Uint8Array<ArrayBuffer>> => {
  if (response.body === null || signal.aborted) {
    throw new Error("Response body unavailable.");
  }
  const reader = response.body.getReader();
  let retained = new Uint8Array(Math.min(maximum, 8192));
  let count = 0;
  const cancelReader = () => {
    void reader.cancel().catch(() => {
      // Observe cancellation refusal without replacing the original delivery result.
    });
  };
  signal.addEventListener("abort", cancelReader, { once: true });
  try {
    while (true) {
      // oxlint-disable-next-line no-await-in-loop -- Consume one bounded stream in order.
      const { value, done } = await reader.read();
      if (signal.aborted) {
        throw new Error("Response consumption cancelled.");
      }
      if (done) {
        break;
      }
      if (value.byteLength > maximum - count) {
        throw new Error("Response exceeds its byte allowance.");
      }
      const required = count + value.byteLength;
      if (required > retained.byteLength) {
        const grown = new Uint8Array(
          Math.min(maximum, Math.max(required, retained.byteLength * 2)),
        );
        grown.set(retained.subarray(0, count));
        retained = grown;
      }
      retained.set(value, count);
      count = required;
    }
    return retained.slice(0, count);
  } finally {
    signal.removeEventListener("abort", cancelReader);
    cancelReader();
    reader.releaseLock();
  }
};

/** Read a declared binary transfer into a single bounded allocation. */
export const exactResponseBytes = async (
  response: Response,
  expected: number,
  signal: AbortSignal,
): Promise<Uint8Array<ArrayBuffer>> => {
  if (response.body === null) {
    if (expected === 0 && !signal.aborted) {
      return new Uint8Array(0);
    }
    throw new Error("Response body unavailable.");
  }
  const reader = response.body.getReader();
  const bytes = new Uint8Array(expected);
  let consumed = 0;
  const cancel = () => {
    void reader.cancel().catch(() => undefined);
  };
  signal.addEventListener("abort", cancel, { once: true });
  try {
    while (!signal.aborted) {
      // oxlint-disable-next-line no-await-in-loop -- Read this bounded body in order.
      const { value, done } = await reader.read();
      if (done || signal.aborted) {
        break;
      }
      if (value.byteLength > expected - consumed) {
        throw new Error("Response exceeds its declared length.");
      }
      bytes.set(value, consumed);
      consumed += value.byteLength;
    }
    if (signal.aborted || consumed !== expected) {
      throw new Error("Response did not match its declared length.");
    }
    return bytes;
  } finally {
    signal.removeEventListener("abort", cancel);
    cancel();
    reader.releaseLock();
  }
};
