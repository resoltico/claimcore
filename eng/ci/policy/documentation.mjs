import { executable } from "../executable.mjs";
// Prove the generated documentation is current and that regenerating it twice leaves every file
// exactly as it was: the check passes, then two writes are byte-idle relative to a fingerprint of
// every file Git would list (tracked, plus untracked and not ignored).
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { sourceFingerprint } from "../source-snapshot.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const tool = join(root, "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll");

/**
 * @param {string[]} args
 * @returns {number} The documentation tool's exit status.
 */
function documentationTool(args) {
  return (
    spawnSync(executable("dotnet"), [tool, ...args], { cwd: root, stdio: "inherit" }).status ?? 1
  );
}

/** @param {string} label @param {string} before */
function writeIdle(label, before) {
  if (documentationTool(["write"]) !== 0) {
    throw new Error(`The ${label} documentation write failed.`);
  }
  if (sourceFingerprint(root) !== before) {
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
    const before = sourceFingerprint(root);
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
