import { spawnSync } from "node:child_process";

/**
 * @typedef {(file: string, args: string[], options: object) => {
 *   status: number | null, stdout?: string | null, stderr?: string | null, error?: Error | undefined
 * }} Execute A synchronous process runner shaped like `spawnSync` with a string encoding.
 */

/** @param {string | null | undefined} stdout */
function parseDocument(stdout) {
  if (!stdout?.trim()) {
    throw new Error("DEPENDENCY_METADATA_EMPTY");
  }
  try {
    return JSON.parse(stdout);
  } catch {
    throw new Error("DEPENDENCY_METADATA_INVALID");
  }
}

/** Whether a failed run looks like a passing network condition rather than a real failure. @param {{ error?: Error | undefined, stderr?: string | null }} result */
function isTransient(result) {
  const code = /** @type {NodeJS.ErrnoException | undefined} */ (result.error)?.code ?? "";
  return (
    ["ETIMEDOUT", "EAI_AGAIN", "ECONNRESET"].includes(code) ||
    /\b(?:EAI_AGAIN|ETIMEDOUT|ECONNRESET|E429|E502|E503|E504)\b/u.test(result.stderr ?? "")
  );
}

/**
 * Run a command that prints JSON, retrying transient network failures.
 * @param {string} file
 * @param {string[]} args
 * @param {string} cwd
 * @param {object} [options]
 * @param {number[]} [options.allowed] Exit codes that still carry a document.
 * @param {Execute} [options.execute]
 * @param {(milliseconds: number) => void} [options.pause]
 * @returns {import("./types.mjs").Json}
 */
export function jsonProcess(
  file,
  args,
  cwd,
  {
    allowed = [0],
    execute = /** @type {Execute} */ (spawnSync),
    pause = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms),
  } = {},
) {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const result = execute(file, args, {
      cwd,
      encoding: "utf8",
      timeout: 120000,
      maxBuffer: 16 * 1024 * 1024,
      shell: false,
    });
    if (!result.error && result.status !== null && allowed.includes(result.status)) {
      return parseDocument(result.stdout);
    }
    const transient = isTransient(result);
    if (!transient || attempt === 2) {
      throw new Error(transient ? "DEPENDENCY_METADATA_UNAVAILABLE" : "DEPENDENCY_COMMAND_FAILED");
    }
    pause((attempt + 1) * 1000);
  }
  throw new Error("DEPENDENCY_METADATA_UNAVAILABLE");
}
