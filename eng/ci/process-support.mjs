import { commandLine } from "./executable.mjs";
// Small process and command-line helpers shared by the stage runner and the local runner.
import { spawn } from "node:child_process";
import { closeSync, existsSync, openSync, readFileSync, writeFileSync } from "node:fs";
import { delimiter, join } from "node:path";

const escape = String.fromCharCode(27);
const ansi = new RegExp(`${escape}\\[[0-9;]*m`, "gu");
const logLimit = 16 * 1024 * 1024;

/** Drain both streams, retaining complete ordinary output and an explicit bounded overflow marker.
 * @param {string} path */
function boundedLog(path) {
  const descriptor = openSync(path, "w", 0o600);
  let observed = 0;
  let captured = 0;
  let failed = false;
  return {
    fail: () => {
      failed = true;
    },
    /** @param {Buffer} bytes */
    write(bytes) {
      observed = Math.min(Number.MAX_SAFE_INTEGER, observed + bytes.length);
      const admitted = bytes.subarray(0, Math.max(0, logLimit - captured));
      if (!failed && admitted.length > 0) {
        try {
          writeFileSync(descriptor, admitted);
          captured += admitted.length;
        } catch {
          failed = true;
        }
      }
    },
    finish() {
      try {
        if (observed > logLimit) {
          writeFileSync(
            descriptor,
            `\n[Log capture exceeded ${logLimit} bytes; observedBytes=${observed}; countSaturated=${observed === Number.MAX_SAFE_INTEGER}; further output omitted; stage refused.]\n`,
          );
        }
      } catch {
        failed = true;
      }
      try {
        closeSync(descriptor);
      } catch {
        failed = true;
      }
      return failed || observed > logLimit;
    },
  };
}

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
    .some(
      (directory) =>
        directory !== "" &&
        ["", ...(process.platform === "win32" ? [".exe", ".cmd", ".bat"] : [])].some((suffix) =>
          existsSync(join(directory, tool + suffix)),
        ),
    );

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
 * start, 128 when a signal ended it). Output beyond 16 MiB is drained and omitted with a bounded
 * marker; an otherwise successful command then fails rather than qualifying incomplete diagnostics.
 * @param {string} command
 * @param {string[]} args
 * @param {{ cwd: string, log: string, env?: NodeJS.ProcessEnv, groups?: number[], onOutcome?:(outcome:{exit:number,captureFailed:boolean})=>void }} options
 * @returns {Promise<number>}
 */
export function runToLog(command, args, { cwd, log, env, groups, onOutcome }) {
  const selected = commandLine(command, args);
  const capture = boundedLog(log);
  return new Promise((resolve) => {
    let child;
    try {
      child = spawn(...selected, {
        cwd,
        detached: groups !== undefined && process.platform !== "win32",
        stdio: ["ignore", "pipe", "pipe"],
        ...(env === undefined ? {} : { env }),
      });
    } catch {
      const captureFailed = capture.finish();
      onOutcome?.({ exit: 127, captureFailed });
      resolve(127);
      return;
    }
    if (groups !== undefined && child.pid !== undefined) {
      groups.push(child.pid);
    }
    let settled = false;
    let startupFailed = false;
    for (const stream of [child.stdout, child.stderr]) {
      stream.on("data", capture.write);
      stream.on("error", capture.fail);
    }
    /** @param {number} status */
    const settle = (status) => {
      if (settled) {
        return;
      }
      settled = true;
      const captureFailed = capture.finish();
      onOutcome?.({ exit: status, captureFailed });
      resolve(status === 0 && captureFailed ? 1 : status);
    };
    child.on("error", () => {
      startupFailed = true;
    });
    child.on("close", (code, signal) => settle(startupFailed ? 127 : (code ?? (signal ? 128 : 1))));
  });
}
