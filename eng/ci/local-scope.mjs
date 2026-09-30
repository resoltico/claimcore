// Which registered jobs a set of changed files can affect.
import { spawnSync } from "node:child_process";

/**
 * @param {string} root
 * @param {string[]} args
 */
const git = (root, args) =>
  spawnSync("git", args, { cwd: root, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });

/**
 * Files changed against `ref` (default: the merge base with origin/main), including uncommitted and
 * untracked ones; null when unknown.
 * @param {string} root
 * @param {string | undefined} ref
 * @returns {string[] | null}
 */
export function changedFiles(root, ref) {
  const base = ref ?? git(root, ["merge-base", "HEAD", "origin/main"]).stdout.trim();
  if (!base) {
    return null;
  }
  const tracked = git(root, ["diff", "--name-only", base]);
  const untracked = git(root, ["ls-files", "--others", "--exclude-standard"]);
  if (tracked.status !== 0 || untracked.status !== 0) {
    return null;
  }
  return [...tracked.stdout.split("\n"), ...untracked.stdout.split("\n")].filter(Boolean);
}

/**
 * Whether `job` must run given the changed files (null means unknown, so it must).
 * @param {{ scope?: string[] | null }} job
 * @param {string[] | null} changed
 */
export function affected(job, changed) {
  if (job.scope === null || job.scope === undefined || changed === null) {
    return true;
  }
  const patterns = job.scope.map((pattern) => new RegExp(pattern, "u"));
  return changed.some((file) => patterns.some((pattern) => pattern.test(file)));
}
