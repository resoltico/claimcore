// Helpers shared by the tests of the governance scripts.
import assert from "node:assert/strict";

/**
 * The value, asserted present, so a lookup that must succeed reads as one expression.
 * @template T
 * @param {T | null | undefined} value
 * @param {string} [message]
 * @returns {T}
 */
export function must(value, message = "Expected a value.") {
  assert.ok(value !== null && value !== undefined, message);
  return value;
}

/**
 * An error carrying an HTTP status, as the GitHub client throws.
 * @param {number} status
 * @returns {Error}
 */
export const statusError = (status) => Object.assign(new Error("status"), { status });
