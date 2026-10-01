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
    const child = spawn(command, args, { cwd, env, stdio: ["ignore", "pipe", "pipe"] });
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

const gitRedirect = new Set([
  "GIT_ALTERNATE_OBJECT_DIRECTORIES",
  "GIT_CEILING_DIRECTORIES",
  "GIT_COMMON_DIR",
  "GIT_CONFIG_COUNT",
  "GIT_CONFIG_PARAMETERS",
  "GIT_DIR",
  "GIT_DISCOVERY_ACROSS_FILESYSTEM",
  "GIT_INDEX_FILE",
  "GIT_NAMESPACE",
  "GIT_OBJECT_DIRECTORY",
  "GIT_WORK_TREE",
]);

/** @returns {NodeJS.ProcessEnv} The environment without anything that redirects git elsewhere. */
export function gitEnvironment() {
  const env = Object.fromEntries(
    Object.entries(process.env).filter(([name]) => !gitRedirect.has(name)),
  );
  return { ...env, GIT_OPTIONAL_LOCKS: "0", GIT_TERMINAL_PROMPT: "0" };
}

/** @returns {NodeJS.ProcessEnv} The environment without scanner configuration overrides. */
export function scannerEnvironment() {
  const env = { ...process.env };
  delete env["GITLEAKS_CONFIG"];
  delete env["GITLEAKS_CONFIG_TOML"];
  return env;
}
