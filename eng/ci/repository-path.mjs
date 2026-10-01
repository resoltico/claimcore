import { lstatSync, readdirSync } from "node:fs";
import { isAbsolute, join, resolve, sep } from "node:path";

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
  const repository = resolve(root);
  let current = repository;
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
  if (!full.startsWith(repository + sep) || !lstatSync(full).isFile()) {
    throw new Error("A source inventory entry is outside the repository or is not a regular file.");
  }
  return full;
}
