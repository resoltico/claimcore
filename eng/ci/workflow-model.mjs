import assert from "node:assert/strict";

export const publisherPaths = new Set(["release.yml"]);
export const standalonePaths = new Set(["verify-properties.yml", ...publisherPaths]);
/** @param {unknown} value */
export const falseInput = (value) => value === false || value === "false";
/** @param {unknown} value @returns {string[]} */
export const names = (value) => {
  if (typeof value === "string") {
    return [value];
  }
  return Array.isArray(value) ? value : Object.keys(/** @type {object} */ (value ?? {}));
};
/** @param {import("./types.mjs").Json} job */
export const dependencies = (job) => names(job.needs);
/** @template T @param {T} text */
export const expression = (text) =>
  typeof text === "string" ? text.replace(/^\$\{\{\s*|\s*\}\}$/gu, "").trim() : text;
/** @param {Iterable<string>} actual @param {Iterable<string>} expected @param {string} message */
export const same = (actual, expected, message) =>
  assert.deepEqual([...actual].sort(), [...expected].sort(), message);
