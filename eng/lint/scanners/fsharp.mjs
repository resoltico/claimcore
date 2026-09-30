import { inline, inlineRules } from "./comments.mjs";

const lint = /fsharplint\s*:\s*disable(?:-next-line|-line)?(?:\s+(?<rules>[A-Za-z0-9_. -]+))?\s*$/i;
const pragma = /#pragma\s+warning\s+disable\s*(?<rules>.*)$/i;
const nowarn = /#nowarn\s+(?<rules>(?:"[0-9]+"\s*)+)/g;
const suppress = /SuppressMessage\s*\(\s*"[^"]*"\s*,\s*"(?<rule>[^"]+)"/g;

/** @param {string} code */
const compilerCode = (code) => (/^\d+$/.test(code) ? `FS${code}` : code);

/**
 * F# compiler, FSharpLint and .NET analyzer suppressions.
 * @param {string} file
 * @param {string[]} lines
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanFSharp(file, lines, text) {
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  lines.forEach((line, index) => {
    const off = lint.exec(line);
    if (off) found.push(...inlineRules(file, "fsharplint", off.groups?.["rules"], lines, index));
    const disabled = pragma.exec(line);
    if (disabled) {
      for (const occurrence of inlineRules(file, "fsc", disabled.groups?.["rules"], lines, index)) {
        found.push({ ...occurrence, rule: compilerCode(occurrence.rule) });
      }
    }
  });
  for (const match of text.matchAll(nowarn)) {
    const line = text.slice(0, match.index).split("\n").length - 1;
    for (const quoted of (match.groups?.["rules"] ?? "").matchAll(/"([0-9]+)"/g)) {
      found.push(inline(file, "fsc", compilerCode(quoted[1] ?? ""), lines, line));
    }
  }
  for (const match of text.matchAll(suppress)) {
    const line = text.slice(0, match.index).split("\n").length - 1;
    found.push(inline(file, "dotnet-analyzer", match.groups?.["rule"] ?? "", lines, line));
  }
  return found;
}
