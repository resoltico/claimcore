// Git owns source membership; consumers own the subset relevant to their responsibility.
import { spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdtempSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { executable } from "./executable.mjs";
import { gitEnvironment } from "./scan/process.mjs";
import { parseNulPaths, resolveSourceFile } from "./repository-path.mjs";

/** @param {string} root @param {string[]} args */
function git(root, args) {
  return spawnSync(executable("git"), args, {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
}

/** @param {string} root @param {string[]} prefix @returns {string[]} */
function enumerate(root, prefix) {
  const listing = git(root, [
    ...prefix,
    "ls-files",
    "--cached",
    "--others",
    "--exclude-per-directory=.gitignore",
    "-z",
    "--",
    ".",
  ]);
  const removed = git(root, [...prefix, "ls-files", "--deleted", "-z", "--", "."]);
  if (listing.status !== 0 || removed.status !== 0) {
    throw new Error("The repository source inventory could not be enumerated.");
  }
  const gone = new Set(parseNulPaths(removed.stdout));
  return [...new Set(parseNulPaths(listing.stdout))].filter((path) => !gone.has(path)).sort();
}

/** @param {string} root @param {string[]} files @returns {string[]} */
function validateFiles(root, files) {
  const portable = new Set();
  for (const file of files) {
    if (portable.has(file.toLowerCase())) {
      throw new Error("The source inventory contains a cross-platform path collision.");
    }
    portable.add(file.toLowerCase());
    resolveSourceFile(root, file);
  }
  return files;
}

/** @param {string} directory @returns {string[]} Sorted visible source paths. */
export function repositoryFiles(directory) {
  if (!existsSync(directory) || lstatSync(directory).isSymbolicLink()) {
    throw new Error("The repository root must be an existing directory without a link.");
  }
  const root = realpathSync(directory);
  const top = git(root, ["rev-parse", "--show-toplevel"]);
  if (top.status === 0 && realpathSync(top.stdout.trim()) === root) {
    return validateFiles(root, enumerate(root, []));
  }
  if (existsSync(join(root, ".git"))) {
    throw new Error("The repository contains an invalid or inaccessible Git worktree marker.");
  }
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-inventory-"));
  try {
    const database = join(scratch, "inventory.git");
    if (git(root, ["init", "--bare", "--quiet", database]).status !== 0) {
      throw new Error("The isolated source inventory could not be initialized.");
    }
    return validateFiles(root, enumerate(root, ["--git-dir", database, "--work-tree", root]));
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  process.stdout.write(JSON.stringify(repositoryFiles(process.argv[2] ?? process.cwd())));
}
