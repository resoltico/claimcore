// A clean preflight checks the working source, not yesterday's HEAD or local build output.
import { createHash } from "node:crypto";
import { copyFileSync, lstatSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { repositoryFiles } from "./repository.mjs";
import { resolveSourceFile } from "./repository-path.mjs";

/** @param {string} root @returns {string} */
export function sourceFingerprint(root) {
  const hash = createHash("sha256");
  for (const path of repositoryFiles(root)) {
    hash.update(
      `${path}\0${lstatSync(join(root, path)).mode & 0o777}\0${createHash("sha256")
        .update(readFileSync(resolveSourceFile(root, path)))
        .digest("hex")}\0`,
    );
  }
  return hash.digest("hex");
}

/** @param {string} root @param {string} destination @param {typeof copyFileSync} [copy] @returns {string} */
export function copySource(root, destination, copy = copyFileSync) {
  const before = sourceFingerprint(root);
  for (const path of repositoryFiles(root)) {
    const output = join(destination, path);
    mkdirSync(dirname(output), { recursive: true, mode: 0o700 });
    if (path === ".local" || path.startsWith(".local/")) {
      throw new Error("Private local state cannot be a clean-source input.");
    }
    copy(resolveSourceFile(root, path), output);
  }
  if (sourceFingerprint(destination) !== before || sourceFingerprint(root) !== before) {
    throw new Error("Source changed while preparing the clean verification tree.");
  }
  return before;
}
