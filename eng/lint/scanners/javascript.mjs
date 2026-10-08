import { parseSync } from "oxc-parser";
import { referencePattern, ruleList } from "../model.mjs";
import { inline } from "./comments.mjs";

const commentStart = String.raw`(?:\/\/|\/\*+)\s*`;
const disable = new RegExp(
  `${commentStart}(?:oxlint|eslint)-disable(?:-(?:next-line|line))?\\s*(?<rules>[^*]*?)(?:\\s+--.*)?(?:\\*\\/)?\\s*$`,
  "iu",
);
const typescript = new RegExp(
  `${commentStart}(?<rule>@ts-(?:ignore|nocheck|expect-error))\\b`,
  "iu",
);
const prettier = new RegExp(`${commentStart}(?<rule>prettier-ignore(?:-start|-end)?)\\b`, "iu");
const stylelint = new RegExp(
  `${commentStart}stylelint-disable(?:-(?:next-line|line))?\\s*(?<rules>[^*]*?)(?:\\s+--.*)?(?:\\*\\/)?\\s*$`,
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
 * @param {string | null} [reference] Reference proven to be in an actual parsed comment.
 * @returns {import("../model.mjs").Occurrence[]}
 */
function fromComment(file, comment, lines, index, reference) {
  return syntaxes.flatMap(({ pattern, tool, rule }) => {
    const match = pattern.exec(comment);
    return match
      ? rule(match).map((name) => {
          const occurrence = inline(file, tool, name, lines, index);
          if (reference !== undefined) {
            occurrence.id = reference;
          }
          return occurrence;
        })
      : [];
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
  const commentEnds = new Map(
    comments.map((comment) => [
      text.slice(0, comment.end).split("\n").length - 1,
      text.slice(comment.start, comment.end),
    ]),
  );
  return comments.flatMap((comment) => {
    const index = text.slice(0, comment.start).split("\n").length - 1;
    const actual = text.slice(comment.start, comment.end);
    const above =
      commentEnds
        .get(index - 1)
        ?.split("\n")
        .at(-1) ?? "";
    const reference = referencePattern.exec(actual) ?? referencePattern.exec(above);
    return fromComment(file, actual, lines, index, reference?.[1]?.toUpperCase() ?? null);
  });
}

/**
 * Suppressions in stylesheets, and the line-by-line fallback for scripts that cannot be parsed.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanStyleComments(file, lines) {
  const text = lines.join("\n");
  const comments = text.matchAll(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/gu);
  return [...comments].flatMap((comment) => {
    const index = text.slice(0, comment.index).split("\n").length - 1;
    return fromComment(file, comment[0], lines, index);
  });
}
