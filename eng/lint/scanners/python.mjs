import { inline, inlineRules, withoutStrings } from "./comments.mjs";

const noqa = /#\s*noqa\b(?::\s*(?<rules>[A-Za-z0-9,\s]+))?/iu;
const ruffFile = /#\s*ruff\s*:\s*(?:noqa|disable)\b(?:\s*[:[]\s*(?<rules>[A-Za-z0-9,\s]+)\]?)?/iu;
const typeIgnore = /#\s*type\s*:\s*ignore\b(?:\[(?<rules>[^\]]*)\])?/iu;
const listedRules = [
  { pattern: noqa, tool: "ruff" },
  { pattern: ruffFile, tool: "ruff" },
  { pattern: typeIgnore, tool: "mypy" },
];
const otherTools = [
  { tool: "mypy", pattern: /#\s*mypy\s*:/iu, rule: "mypy-inline-config" },
  {
    tool: "mypy",
    pattern: /#\s*pyright\s*:|#\s*pyrefly\s*:|#\s*ty\s*:/iu,
    rule: "type-checker-inline-config",
  },
  { tool: "ruff", pattern: /#\s*(?:fmt|yapf)\s*:\s*(?:off|skip)\b/iu, rule: "format-off" },
  {
    tool: "ruff",
    pattern: /#\s*(?:pylint\s*:|nosec\b|isort\s*:)/iu,
    rule: "foreign-linter-directive",
  },
  { tool: "coverage", pattern: /#\s*pragma\s*:\s*no\s+cover\b/iu, rule: "pragma-no-cover" },
];

/**
 * The code on one line with anything inside a docstring or string literal removed.
 * @param {string} raw
 * @param {{ triple: string }} state Whether a triple-quoted string is open across lines.
 * @returns {string | null} The remaining code, or null when the whole line is inside a docstring.
 */
function codeOf(raw, state) {
  let line = raw;
  if (state.triple !== "") {
    const end = line.indexOf(state.triple);
    if (end < 0) {
      return null;
    }
    line = line.slice(end + 3);
    state.triple = "";
  }
  const opening = /("""|''')/u.exec(line);
  if (opening && !line.slice((opening.index ?? 0) + 3).includes(opening[1] ?? "")) {
    state.triple = opening[1] ?? "";
    line = line.slice(0, opening.index);
  }
  return withoutStrings(line);
}

/**
 * Python suppression comments. Text inside string literals and docstrings is ignored.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanPython(file, lines) {
  const state = { triple: "" };
  return lines.flatMap((raw, index) => {
    const code = codeOf(raw, state);
    if (code === null) {
      return [];
    }
    const listed = listedRules.flatMap(({ pattern, tool }) => {
      const match = pattern.exec(code);
      return match ? inlineRules(file, tool, match.groups?.["rules"], lines, index) : [];
    });
    const others = otherTools
      .filter((other) => other.pattern.test(code))
      .map((other) => inline(file, other.tool, other.rule, lines, index));
    return [...listed, ...others];
  });
}
