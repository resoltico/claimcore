import assert from "node:assert/strict";

export const publisherPaths = new Set([
  "release.yml",
  "publish-postgres-image.yml",
]);
export const standalonePaths = new Set([
  "verify-properties.yml",
  "dependency-health.yml",
  ...publisherPaths,
]);
export const falseInput = (value) => value === false || value === "false";
export const names = (value) =>
  typeof value === "string"
    ? [value]
    : Array.isArray(value)
      ? value
      : Object.keys(value ?? {});
export const dependencies = (job) => names(job.needs);
export const expression = (text) =>
  typeof text === "string"
    ? text.replace(/^\$\{\{\s*|\s*\}\}$/gu, "").trim()
    : text;
export const same = (actual, expected, message) =>
  assert.deepEqual([...actual].sort(), [...expected].sort(), message);
