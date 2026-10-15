import { requireUniqueJsonMembers } from "./jsonMemberNames";
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

export const strictJsonBytes = (bytes: Uint8Array): unknown => {
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  requireUniqueJsonMembers(text);
  const value: unknown = JSON.parse(text);
  const pending: unknown[] = [value];
  const valid = (content: string): boolean =>
    Array.from(content).every((character) => {
      const scalar = character.codePointAt(0)!;
      return (
        scalar !== 0 &&
        !(scalar >= 0xd800 && scalar <= 0xdfff) &&
        !(scalar >= 0xfdd0 && scalar <= 0xfdef) &&
        (scalar & 0xffff) < 0xfffe
      );
    });
  while (pending.length > 0) {
    const current = pending.pop();
    if (typeof current === "string" && !valid(current)) {
      throw new Error("JSON text is not representable.");
    }
    if (Array.isArray(current)) {
      for (const item of current) {
        pending.push(item);
      }
    } else if (typeof current === "object" && current !== null) {
      for (const [key, item] of Object.entries(current)) {
        if (!valid(key)) {
          throw new Error("JSON member name is not representable.");
        }
        pending.push(item);
      }
    }
  }
  return value;
};
