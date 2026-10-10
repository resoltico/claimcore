import { byteCapture } from "../byte-capture.mjs";
import { commandLine } from "../executable.mjs";
// Running the scanner and git as child processes with a bounded lifetime and a scrubbed environment.
import { spawn } from "node:child_process";

/**
 * @typedef {object} ChildResult
 * @property {number} status
 * @property {string} stdout
 * @property {string} stderr
 * @property {boolean} overflow
 */

/**
 * @param {string} command
 * @param {string[]} args
 * @param {{ cwd: string, env: NodeJS.ProcessEnv, timeoutMs?: number }} options
 * @returns {Promise<ChildResult>}
 */
export function runChild(command, args, { cwd, env, timeoutMs = 300_000 }) {
  return new Promise((resolve, reject) => {
    const child = spawn(...commandLine(command, args), {
      cwd,
      env,
      stdio: ["ignore", "pipe", "pipe"],
    });
    const capture = byteCapture();
    child.stdout.on("data", capture.stdout);
    child.stderr.on("data", capture.stderr);
    const deadline = childDeadline(child, timeoutMs, reject);
    let deliveryFailed = false;
    const failedDelivery = () => {
      deliveryFailed = true;
    };
    for (const stream of [child.stdout, child.stderr]) {
      stream.on("error", failedDelivery);
    }
    child.on("error", (error) => {
      deadline.clear();
      reject(error);
    });
    child.on("close", (code) => {
      deadline.clear();
      if (deadline.expired()) {
        reject(
          new Error(
            `A required child process timed out; child exit ${code ?? "unknown"}; owned console delivery closed.`,
          ),
        );
        return;
      }
      if (deliveryFailed) {
        reject(
          new Error(`Required child console delivery failed; child exit ${code ?? "unknown"}.`),
        );
        return;
      }
      resolve({
        status: code ?? 1,
        ...capture.result(),
      });
    });
  });
}

/** @returns {NodeJS.ProcessEnv} Git cannot inherit another repository or replacement view. */
export function gitEnvironment() {
  const env = Object.fromEntries(
    Object.entries(process.env).filter(([name]) => !name.startsWith("GIT_")),
  );
  return {
    ...env,
    GIT_OPTIONAL_LOCKS: "0",
    GIT_TERMINAL_PROMPT: "0",
    GIT_NO_REPLACE_OBJECTS: "1",
    GIT_NO_LAZY_FETCH: "1",
  };
}

/** @param {NodeJS.ProcessEnv} [environment] @returns {NodeJS.ProcessEnv} */
export function scannerEnvironment(environment = process.env) {
  const env = { ...environment };
  delete env["GITLEAKS_CONFIG"];
  delete env["GITLEAKS_CONFIG_TOML"];
  return env;
}

/** Child exit does not prove descendant or inherited-pipe settlement.
 * @param {import("node:child_process").ChildProcess} child @param {number} timeoutMs
 * @param {(error:Error)=>void} reject */
function childDeadline(child, timeoutMs, reject) {
  let expired = false;
  /** @type {NodeJS.Timeout | undefined} */
  let settlement;
  const timer = setTimeout(() => {
    expired = true;
    child.kill("SIGKILL");
    settlement = setTimeout(() => {
      reject(
        new Error(
          `Required child timed out; child exit ${child.exitCode ?? "unknown"}; descendant or pipe settlement remains unknown.`,
        ),
      );
      child.stdout?.destroy();
      child.stderr?.destroy();
      child.unref();
    }, 2000);
  }, timeoutMs);
  return {
    expired: () => expired,
    clear: () => {
      clearTimeout(timer);
      clearTimeout(settlement);
    },
  };
}
