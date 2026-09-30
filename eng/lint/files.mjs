import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, relative, sep } from "node:path";

const roots = ["src", "tests", "web", "eng", "config", "db", ".github", "docs"];
/** Version-control data, dependency trees and tool caches are not source. */
const skipped = new Set([
  ".git",
  "node_modules",
  "__pycache__",
  ".venv",
  ".ruff_cache",
  ".mypy_cache",
  "artifacts",
  "bin",
  "obj",
  "TestResults",
]);

/**
 * @typedef {object} SourceFile
 * @property {string} path Repository-relative path with forward slashes.
 * @property {string} text
 * @property {string[]} lines
 */

/** @param {string} value */
const slash = (value) => value.split(sep).join("/");

/**
 * @param {string} directory
 * @param {string} root
 * @param {string[]} excluded Repository-relative prefixes.
 * @param {string[]} into
 */
function walk(directory, root, excluded, into) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    const relativePath = slash(relative(root, path));
    if (entry.isDirectory()) {
      if (
        !skipped.has(entry.name) &&
        !excluded.some((prefix) => `${relativePath}/`.startsWith(prefix))
      ) {
        walk(path, root, excluded, into);
      }
    } else if (entry.isFile()) {
      into.push(relativePath);
    }
  }
}

/**
 * Every tracked-looking file under the scanned roots plus the repository root's own files.
 * @param {string} root
 * @param {string[]} excluded Generated-output prefixes from the registry.
 * @returns {string[]}
 */
export function repositoryFiles(root, excluded) {
  /** @type {string[]} */
  const found = [];
  for (const name of roots) {
    const directory = join(root, name);
    if (existsSync(directory)) walk(directory, root, excluded, found);
  }
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (entry.isFile()) found.push(entry.name);
  }
  return found.sort();
}

/**
 * @param {string} root
 * @param {string} path
 * @returns {SourceFile}
 */
export function readSource(root, path) {
  const text = readFileSync(join(root, path), "utf8");
  return { path, text, lines: text.split(/\r?\n/) };
}
