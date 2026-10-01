// Scan the repository's source files for credentials. The files git would list (tracked, plus
// untracked and not ignored) are copied into a private snapshot and scanned there with the
// scanner's own rules; a repository-local scanner configuration is refused rather than honoured.
import {
  chmodSync,
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, resolve, sep } from "node:path";
import { gitEnvironment, runChild, scannerEnvironment } from "./process.mjs";

/**
 * @param {string} raw NUL-terminated `git ls-files -z` output.
 * @returns {string[]}
 */
export function parseNulPaths(raw) {
  if (raw.length > 0 && !raw.endsWith("\0")) {
    throw new Error("Git returned a non-terminated source inventory.");
  }
  const paths = raw.split("\0").slice(0, -1);
  if (paths.some((path) => path === "")) {
    throw new Error("Git returned an empty source path.");
  }
  return paths;
}

/** @typedef {(args: string[]) => Promise<import("./process.mjs").ChildResult>} Git */

/**
 * The files git lists for a working tree, minus tracked files deleted from it.
 * @param {Git} git
 * @returns {Promise<string[]>}
 */
async function listWorktree(git) {
  const listing = await git([
    "ls-files",
    "--cached",
    "--others",
    "--exclude-per-directory=.gitignore",
    "-z",
    "--",
    ".",
  ]);
  const removed = await git(["ls-files", "--deleted", "-z", "--", "."]);
  if (listing.status !== 0 || removed.status !== 0) {
    throw new Error("The source inventory could not be enumerated.");
  }
  // A tracked file deleted from the working tree is not source; the snapshot is the working tree.
  const gone = new Set(parseNulPaths(removed.stdout));
  return parseNulPaths(listing.stdout).filter((path) => !gone.has(path));
}

/**
 * The files a directory that is not a git worktree would have, by its .gitignore files alone.
 * @param {Git} git
 * @param {string} root
 * @param {string} scratch
 * @returns {Promise<string[]>}
 */
async function listPlainDirectory(git, root, scratch) {
  const bare = join(scratch, "inventory.git");
  if ((await git(["init", "--bare", "--quiet", bare])).status !== 0) {
    throw new Error("The temporary source inventory could not be initialized.");
  }
  const listing = await git([
    "--git-dir",
    bare,
    "--work-tree",
    root,
    "ls-files",
    "--others",
    "--exclude-per-directory=.gitignore",
    "-z",
    "--",
    ".",
  ]);
  if (listing.status !== 0) {
    throw new Error("The source inventory could not be enumerated.");
  }
  return parseNulPaths(listing.stdout);
}

/**
 * @param {string} root
 * @param {string} scratch
 * @returns {Promise<string[]>}
 */
async function inventory(root, scratch) {
  const env = gitEnvironment();
  /** @type {Git} */
  const git = (args) => runChild("git", args, { cwd: root, env, timeoutMs: 60_000 });
  const probe = await git(["rev-parse", "--is-inside-work-tree"]);
  const inside = probe.status === 0 && probe.stdout.trim() === "true";
  if (!inside && existsSync(join(root, ".git"))) {
    throw new Error("The repository contains an invalid or inaccessible Git worktree marker.");
  }
  return inside ? listWorktree(git) : listPlainDirectory(git, root, scratch);
}

/**
 * Resolve an inventory path to a regular file inside `root`, exact in case and free of links.
 * @param {string} root
 * @param {string} relative
 * @returns {string}
 */
export function resolveSourceFile(root, relative) {
  if (
    relative.trim() === "" ||
    isAbsolute(relative) ||
    relative.includes("\\") ||
    relative.includes("\0")
  ) {
    throw new Error("The source inventory contains an unsafe path.");
  }
  const segments = relative.split("/");
  if (segments.some((segment) => ["", ".", ".."].includes(segment))) {
    throw new Error("The source inventory contains an unsafe path segment.");
  }
  let current = root;
  for (const segment of segments) {
    if (!readdirSync(current).includes(segment)) {
      throw new Error("A source path is missing or has different casing.");
    }
    current = join(current, segment);
    if (lstatSync(current).isSymbolicLink()) {
      throw new Error("Source inventory paths may not contain symbolic links or junctions.");
    }
  }
  const full = resolve(current);
  if (!full.startsWith(root + sep) || !lstatSync(full).isFile()) {
    throw new Error("A source inventory entry is outside the repository or is not a regular file.");
  }
  return full;
}

/**
 * @param {string} root
 * @param {string[]} relativePaths
 * @param {string} snapshot
 */
function copyInventory(root, relativePaths, snapshot) {
  const exact = new Set();
  const portable = new Set();
  for (const relative of relativePaths) {
    if (exact.has(relative) || portable.has(relative.toLowerCase())) {
      throw new Error(
        "The source inventory contains a duplicate or cross-platform path collision.",
      );
    }
    exact.add(relative);
    portable.add(relative.toLowerCase());
    if (relative === ".gitleaks.toml") {
      throw new Error(
        "Repository-local Gitleaks configuration is forbidden by the source-scan policy.",
      );
    }
    const destination = join(snapshot, ...relative.split("/"));
    mkdirSync(dirname(destination), { recursive: true });
    copyFileSync(resolveSourceFile(root, relative), destination);
  }
}

/**
 * @param {{ root: string, gitleaks: string, stdout?: (text: string) => void, stderr?: (text: string) => void }} options
 * @returns {Promise<number>} The scanner's exit status; 0 means clean.
 */
export async function scanSource({
  root,
  gitleaks,
  stdout = (text) => process.stdout.write(text),
  stderr = (text) => process.stderr.write(text),
}) {
  const repository = resolve(root).replace(/[\\/]+$/u, "");
  if (!existsSync(repository) || lstatSync(repository).isSymbolicLink()) {
    throw new Error("The repository root does not exist or is a link.");
  }
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-source-scan-"));
  try {
    chmodSync(scratch, 0o700);
    const snapshot = join(scratch, "source");
    mkdirSync(snapshot);
    const emptyIgnore = join(scratch, "empty.gitleaksignore");
    writeFileSync(emptyIgnore, "");
    const files = await inventory(repository, scratch);
    if (files.length === 0) {
      throw new Error("The source inventory is empty.");
    }
    copyInventory(repository, files, snapshot);
    const result = await runChild(
      gitleaks,
      [
        "dir",
        "--redact",
        "--no-banner",
        "--no-color",
        "--exit-code",
        "1",
        "--ignore-gitleaks-allow",
        "--gitleaks-ignore-path",
        emptyIgnore,
        snapshot,
      ],
      { cwd: repository, env: scannerEnvironment() },
    );
    stdout(result.stdout);
    stderr(result.stderr);
    if (result.status === 0) {
      stdout(`Source secret scan passed for ${files.length} repository files.\n`);
    }
    return result.status;
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}
