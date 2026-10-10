// Console retention is byte-bounded across both pipes; listeners keep draining after overflow.
export const consoleMaximumBytes = 16 * 1024 * 1024;

/** @param {number} [maximum] */
export function byteCapture(maximum = consoleMaximumBytes) {
  if (!Number.isSafeInteger(maximum) || maximum < 1) {
    throw new Error("Console capture requires a positive byte budget.");
  }
  let retained = 0;
  let overflow = false;
  /** @type {Buffer[]} */
  const stdout = [];
  /** @type {Buffer[]} */
  const stderr = [];
  /** @param {Buffer[]} destination @param {Buffer} chunk */
  const append = (destination, chunk) => {
    const count = Math.min(chunk.length, maximum - retained);
    if (count !== chunk.length) {
      overflow = true;
    }
    if (count > 0) {
      // Copy: a slice of a giant chunk would retain its entire backing allocation.
      destination.push(Buffer.from(chunk.subarray(0, count)));
      retained += count;
    }
  };
  return {
    /** @param {Buffer} chunk */
    stdout: (chunk) => append(stdout, chunk),
    /** @param {Buffer} chunk */
    stderr: (chunk) => append(stderr, chunk),
    result: () => ({
      stdout: Buffer.concat(stdout).toString("utf8"),
      stderr: Buffer.concat(stderr).toString("utf8"),
      overflow,
      retained,
    }),
  };
}
