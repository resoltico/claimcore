// Prove the generated documentation is current and that regenerating it twice leaves every file
// exactly as it was: the check passes, then two writes are byte-idle relative to a fingerprint of
// every file Git would list (tracked, plus untracked and not ignored).
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gitEnvironment } from "../scan/process.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const tool = join(root, "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll");

/**
 * @param {string[]} args
 * @returns {number} The documentation tool's exit status.
 */
function documentationTool(args) {
  return spawnSync("dotnet", [tool, ...args], { cwd: root, stdio: "inherit" }).status ?? 1;
}

/** @returns {string} A digest of the names and bytes of every listed file. */
export function sourceFingerprint() {
  const listing = spawnSync(
    "git",
    ["ls-files", "-z", "--cached", "--others", "--exclude-standard"],
    {
      cwd: root,
      env: gitEnvironment(),
      maxBuffer: 64 * 1024 * 1024,
    },
  );
  if (listing.status !== 0) {
    throw new Error(
      "The source inventory could not be listed; the repository must be a Git worktree.",
    );
  }
  const hash = createHash("sha256");
  for (const path of listing.stdout.toString("utf8").split("\0").filter(Boolean).sort()) {
    const file = join(root, path);
    hash.update(
      `${path}\0${existsSync(file) ? createHash("sha256").update(readFileSync(file)).digest("hex") : "deleted"}\0`,
    );
  }
  return hash.digest("hex");
}

/** @param {string} label @param {string} before */
function writeIdle(label, before) {
  if (documentationTool(["write"]) !== 0) {
    throw new Error(`The ${label} documentation write failed.`);
  }
  if (sourceFingerprint() !== before) {
    throw new Error(
      `The ${label} documentation write changed source relative to its starting state.`,
    );
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    if (!existsSync(tool)) {
      throw new Error("Build ClaimCore.Docs before checking documentation.");
    }
    const before = sourceFingerprint();
    if (documentationTool(["check"]) !== 0) {
      throw new Error("Documentation check failed.");
    }
    writeIdle("first", before);
    writeIdle("second", before);
    process.stdout.write(
      "Documentation check and both byte-idle writes passed without changing existing user edits.\n",
    );
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
