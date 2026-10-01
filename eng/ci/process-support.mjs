// Small process and command-line helpers shared by the stage runner and the local runner.
import { spawn } from "node:child_process";
import { closeSync, existsSync, openSync, readFileSync } from "node:fs";
import { delimiter, join } from "node:path";

const escape = String.fromCharCode(27);
const ansi = new RegExp(`${escape}\\[[0-9;]*m`, "gu");

/**
 * The value after `--name` in `argv`, or `fallback`.
 * @template {string | undefined} T
 * @param {string[]} argv
 * @param {string} name
 * @param {T} fallback
 * @returns {string | T}
 */
export function option(argv, name, fallback) {
  const at = argv.indexOf(`--${name}`);
  return at >= 0 && argv[at + 1] !== undefined ? /** @type {string} */ (argv[at + 1]) : fallback;
}

/** @param {string[]} argv @param {string} name */
export const flag = (argv, name) => argv.includes(`--${name}`);

/** @param {string} tool */
export const onPath = (tool) =>
  (process.env["PATH"] ?? "")
    .split(delimiter)
    .some((directory) => directory !== "" && existsSync(join(directory, tool)));

/**
 * The last non-empty lines of a log without terminal colour codes, each cut to `width`.
 * @param {string} path
 * @param {number} lines
 * @param {number} width
 * @returns {string[]}
 */
export function logTailLines(path, lines, width) {
  return readFileSync(path, "utf8")
    .replace(ansi, "")
    .split("\n")
    .filter((line) => line.trim() !== "")
    .slice(-lines)
    .map((line) => line.slice(0, width));
}

/**
 * Run a command with its output written to `log`; resolves with its exit status (127 when it cannot
 * start, 128 when a signal ended it).
 * @param {string} command
 * @param {string[]} args
 * @param {{ cwd: string, log: string, env?: NodeJS.ProcessEnv }} options
 * @returns {Promise<number>}
 */
export function runToLog(command, args, { cwd, log, env }) {
  const descriptor = openSync(log, "w");
  return new Promise((resolve) => {
    const child = spawn(command, args, {
      cwd,
      stdio: ["ignore", descriptor, descriptor],
      ...(env === undefined ? {} : { env }),
    });
    /** @param {number} status */
    const settle = (status) => {
      closeSync(descriptor);
      resolve(status);
    };
    child.on("error", () => settle(127));
    child.on("close", (code, signal) => settle(code ?? (signal ? 128 : 1)));
  });
}
