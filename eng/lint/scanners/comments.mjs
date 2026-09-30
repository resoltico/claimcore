import { referenceNear, ruleList } from "../model.mjs";

/**
 * @param {string} file
 * @param {string} tool
 * @param {string} rule
 * @param {string[]} lines
 * @param {number} index
 * @returns {import("../model.mjs").Occurrence}
 */
export function inline(file, tool, rule, lines, index) {
  return { file, tool, rule, kind: "inline", line: index + 1, id: referenceNear(lines, index) };
}

/**
 * Occurrences for every rule named by one suppression comment.
 * @param {string} file
 * @param {string} tool
 * @param {string | undefined} rules Raw rule list, empty for a blanket suppression.
 * @param {string[]} lines
 * @param {number} index
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function inlineRules(file, tool, rules, lines, index) {
  return ruleList(rules).map((rule) => inline(file, tool, rule, lines, index));
}

/**
 * Remove single-line quoted strings so that a `#` inside one is not read as a comment.
 * @param {string} line
 */
export function withoutStrings(line) {
  return line.replace(/"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'/g, '""');
}
