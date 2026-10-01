import { parseSync } from "oxc-parser";
import { ruleList } from "../model.mjs";
import { inline } from "./comments.mjs";

const commentStart = String.raw`(?:\/\/|\/\*+)\s*`;
const disable = new RegExp(
  `${commentStart}(?:oxlint|eslint)-disable(?:-(?:next-line|line))?\\s*(?<rules>[^*\\r\\n]*?)(?:\\s+--.*)?(?:\\*\\/)?\\s*$`,
  "iu",
);
const typescript = new RegExp(
  `${commentStart}(?<rule>@ts-(?:ignore|nocheck|expect-error))\\b`,
  "iu",
);
const prettier = new RegExp(`${commentStart}(?<rule>prettier-ignore(?:-start|-end)?)\\b`, "iu");
const stylelint = new RegExp(
  `${commentStart}stylelint-disable(?:-(?:next-line|line))?\\s*(?<rules>[^*\\r\\n]*?)(?:\\s+--.*)?(?:\\*\\/)?\\s*$`,
  "iu",
);
const coverage = new RegExp(
  `${commentStart}(?<tool>istanbul|c8|vitest|v8)\\s+ignore(?:\\s+(?<mode>next|if|else|file|start|stop))?\\b`,
  "iu",
);

/**
 * How each suppression syntax maps to a tool and a rule.
 * @type {Array<{ pattern: RegExp, tool: string, rule: (match: RegExpExecArray) => string[] }>}
 */
const syntaxes = [
  { pattern: disable, tool: "oxlint", rule: (match) => ruleList(match.groups?.["rules"]) },
  {
    pattern: typescript,
    tool: "tsc",
    rule: (match) => [(match.groups?.["rule"] ?? "").toLowerCase()],
  },
  {
    pattern: prettier,
    tool: "prettier",
    rule: (match) => [(match.groups?.["rule"] ?? "").toLowerCase()],
  },
  { pattern: stylelint, tool: "stylelint", rule: (match) => ruleList(match.groups?.["rules"]) },
  {
    pattern: coverage,
    tool: "coverage",
    rule: (match) => [
      `${(match.groups?.["tool"] ?? "").toLowerCase()}-ignore-${(match.groups?.["mode"] ?? "unspecified").toLowerCase()}`,
    ],
  },
];

/**
 * The suppressions one comment carries.
 * @param {string} file
 * @param {string} comment Comment text including its delimiters.
 * @param {string[]} lines
 * @param {number} index Zero-based line on which the comment starts.
 * @returns {import("../model.mjs").Occurrence[]}
 */
function fromComment(file, comment, lines, index) {
  return syntaxes.flatMap(({ pattern, tool, rule }) => {
    const match = pattern.exec(comment);
    return match ? rule(match).map((name) => inline(file, tool, name, lines, index)) : [];
  });
}

/**
 * TypeScript and JavaScript suppressions, read from real comments so that suppression-like text in
 * a string or template literal is not one. A file that does not parse is scanned line by line, which
 * can only over-report.
 * @param {string} file
 * @param {string} text
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanScriptComments(file, text, lines) {
  const { comments, errors } = parseSync(file, text);
  if (errors.length > 0) {
    return scanStyleComments(file, lines);
  }
  return comments.flatMap((comment) => {
    const index = text.slice(0, comment.start).split("\n").length - 1;
    return fromComment(file, text.slice(comment.start, comment.end), lines, index);
  });
}

/**
 * Suppressions in stylesheets, and the line-by-line fallback for scripts that cannot be parsed.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanStyleComments(file, lines) {
  return lines.flatMap((line, index) => fromComment(file, line, lines, index));
}
