// Shared shapes for the central lint-exception system.

/**
 * One suppression found in the tree.
 * @typedef {object} Occurrence
 * @property {string} file Repository-relative path with forward slashes.
 * @property {string} tool The tool being told to look away, for example `ruff` or `oxlint`.
 * @property {string} rule The exact rule, code or configuration target.
 * @property {"inline" | "config"} kind Where the suppression lives.
 * @property {number} line One-based line, for messages only; never part of an identity.
 * @property {string | null} id The `lint-exception` reference found beside an inline suppression.
 */

/**
 * One reviewed exception in `config/lint-exceptions.json`.
 * @typedef {object} LintException
 * @property {string} id Stable identifier, `LX-` and four digits.
 * @property {string} tool
 * @property {string[]} rules
 * @property {string} file
 * @property {"inline" | "config"} kind
 * @property {number} count The exact number of occurrences the entry covers.
 * @property {string} reason
 * @property {string} owner
 * @property {string} [reviewOn]
 * @property {string} [expiresOn]
 */

/**
 * A generated-output path excluded from source scans.
 * @typedef {object} GeneratedExclusion
 * @property {string} path
 * @property {string} generator
 * @property {string} reason
 * @property {string} owner
 * @property {string} [reviewOn]
 * @property {string} [expiresOn]
 */

/**
 * @typedef {object} Registry
 * @property {GeneratedExclusion[]} generated
 * @property {LintException[]} exceptions
 */

/** Collects every finding so one run reports all of them. */
export class Report {
  /** @type {string[]} */
  errors = [];

  /** @param {string} message */
  add(message) {
    this.errors.push(message);
  }
}

/** A `lint-exception: LX-0000` reference. */
export const referencePattern = /lint-exception\s*:\s*(LX-\d{4})\b/iu;

/**
 * The reference beside a suppression: on its own line or on the line above it.
 * @param {string[]} lines
 * @param {number} index Zero-based index of the suppression line.
 * @returns {string | null}
 */
export function referenceNear(lines, index) {
  const current = referencePattern.exec(lines[index] ?? "");
  if (current) {
    return (current[1] ?? "").toUpperCase();
  }
  const above = referencePattern.exec(lines[index - 1] ?? "");
  return above ? (above[1] ?? "").toUpperCase() : null;
}

/**
 * Split a rule list such as `a, b;c` into exact rule names; an empty list means every rule.
 * @param {string | undefined} text
 * @returns {string[]}
 */
export function ruleList(text) {
  const rules = (text ?? "").split(/[\s,;]+/u).filter((part) => part !== "");
  return rules.length === 0 ? ["*"] : rules;
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
export const isObject = (value) =>
  typeof value === "object" && value !== null && !Array.isArray(value);

/**
 * @param {unknown} value
 * @returns {Record<string, unknown>}
 */
export const table = (value) => (isObject(value) ? value : {});

/**
 * @param {unknown} value
 * @returns {string[]}
 */
export const strings = (value) => (Array.isArray(value) ? value.map(String) : []);
