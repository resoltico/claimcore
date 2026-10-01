// File-system walking shared by the secret scans: link-free, regular-file-only trees and a
// content fingerprint, so a scan can prove what it looked at and that it did not change.
import { createHash } from "node:crypto";
import { lstatSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { compareOrdinal } from "../suites/inventory.mjs";

/**
 * Every ancestor of `path`, and `path` itself, must be a real directory entry, not a link.
 * @param {string} path Absolute.
 */
export function assertNoLinkAbove(path) {
  for (let current = path; ; current = dirname(current)) {
    if (lstatSync(current).isSymbolicLink()) {
      throw new Error("Path contains a link.");
    }
    if (dirname(current) === current) {
      return;
    }
  }
}

/**
 * The regular files below `path` (or `path` itself), sorted; links and special files are refused.
 * @param {string} path Absolute.
 * @param {{ forbiddenNames?: string[] }} [options] File names that may not appear anywhere.
 * @returns {string[]}
 */
export function regularFiles(path, { forbiddenNames = [] } = {}) {
  /** @type {string[]} */
  const files = [];
  /** @type {string[]} */
  const pending = [path];
  for (let entry = pending.pop(); entry !== undefined; entry = pending.pop()) {
    const stat = lstatSync(entry);
    if (stat.isSymbolicLink()) {
      throw new Error("Tree contains a link.");
    }
    if (forbiddenNames.includes(entry.slice(dirname(entry).length + 1))) {
      throw new Error("Tree contains a scanner override.");
    }
    if (stat.isDirectory()) {
      pending.push(...readdirSync(entry).map((child) => join(entry, child)));
    } else if (stat.isFile()) {
      files.push(entry);
    } else {
      throw new Error("Tree contains a special file.");
    }
  }
  return files.sort(compareOrdinal);
}

/**
 * Hash the names and contents of every file below `path`.
 * @param {string} path Absolute.
 * @returns {string}
 */
export function fingerprint(path) {
  const hash = createHash("sha256");
  for (const file of regularFiles(resolve(path))) {
    const content = createHash("sha256").update(readFileSync(file)).digest("hex");
    hash.update(`${file}\0${content}\0`);
  }
  return hash.digest("hex");
}
