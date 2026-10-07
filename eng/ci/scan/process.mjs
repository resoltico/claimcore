import { commandLine } from "../executable.mjs";
// Running the scanner and git as child processes with a bounded lifetime and a scrubbed environment.
import { spawn } from "node:child_process";

/**
 * @typedef {object} ChildResult
 * @property {number} status
 * @property {string} stdout
 * @property {string} stderr
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
    /** @type {Buffer[]} */
    const out = [];
    /** @type {Buffer[]} */
    const err = [];
    child.stdout.on("data", (chunk) => out.push(chunk));
    child.stderr.on("data", (chunk) => err.push(chunk));
    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error("A required child process timed out."));
    }, timeoutMs);
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({
        status: code ?? 1,
        stdout: Buffer.concat(out).toString("utf8"),
        stderr: Buffer.concat(err).toString("utf8"),
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
