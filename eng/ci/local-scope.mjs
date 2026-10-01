import { executable } from "./executable.mjs";
// Which registered jobs a set of changed files can affect.
import { gitEnvironment } from "./scan/process.mjs";
import { parseNulPaths } from "./repository-path.mjs";
import { spawnSync } from "node:child_process";

/**
 * @param {string} root
 * @param {string[]} args
 */
const git = (root, args) =>
  spawnSync(executable("git"), args, {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
  });

/**
 * Files changed against `ref` (default: the merge base with origin/main), including uncommitted and
 * untracked ones; null when unknown.
 * @param {string} root
 * @param {string | undefined} ref
 * @returns {string[] | null}
 */
export function changedFiles(root, ref) {
  const probe = ref === undefined ? git(root, ["merge-base", "HEAD", "origin/main"]) : undefined;
  if (probe && probe.status !== 0) {
    return null;
  }
  const base = ref ?? probe?.stdout.trim();
  if (!base) {
    return null;
  }
  const tracked = git(root, ["diff", "--name-only", "-z", base]);
  const untracked = git(root, ["ls-files", "--others", "--exclude-per-directory=.gitignore", "-z"]);
  if (tracked.status !== 0 || untracked.status !== 0) {
    return null;
  }
  return [...new Set([...parseNulPaths(tracked.stdout), ...parseNulPaths(untracked.stdout)])];
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
