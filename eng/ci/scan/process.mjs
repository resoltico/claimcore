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
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGKILL");
    }, timeoutMs);
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      if (timedOut) {
        reject(new Error("A required child process timed out after termination settled."));
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
