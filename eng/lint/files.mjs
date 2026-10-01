import { readFileSync } from "node:fs";
import { join } from "node:path";
import { repositoryFiles as sourceFiles } from "../ci/repository.mjs";

/** @typedef {{ path: string, text: string, lines: string[] }} SourceFile */

/** @param {string} root @param {string[]} excluded @returns {string[]} */
export function repositoryFiles(root, excluded) {
  return sourceFiles(root).filter((path) => !excluded.some((prefix) => path.startsWith(prefix)));
}

/**
 * @param {string} root
 * @param {string} path
 * @returns {SourceFile}
 */
export function readSource(root, path) {
  const text = readFileSync(join(root, path), "utf8");
  return { path, text, lines: text.split(/\r?\n/u) };
}
