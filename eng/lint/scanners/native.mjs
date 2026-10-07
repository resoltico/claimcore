import { inline } from "./comments.mjs";

/** Native compiler diagnostic exclusions in admitted C and header source.
 * @param {string} file
 * @param {string} text
 * @param {string[]} lines
 */
export function scanNative(file, text, lines) {
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  for (const match of text.matchAll(/(?:^\s*#\s*pragma[^\n]*|_Pragma[^\n]*)\\\r?\n/gmu)) {
    const index = text.slice(0, match.index).split("\n").length - 1;
    found.push(inline(file, "native-compiler", "*", lines, index));
  }
  const normalized = text.replaceAll('\\"', '"');
  const pattern =
    /(?:^\s*#\s*pragma\s+|_Pragma\s*\(\s*")(?:(?:GCC|clang)\s+diagnostic\s+ignored\s+"(?<warning>[^"\r\n]+)"|warning\s*\(\s*disable\s*:\s*(?<codes>[0-9\s]+)\))/gmu;
  for (const match of normalized.matchAll(pattern)) {
    const index = normalized.slice(0, match.index).split("\n").length - 1;
    const rules = match.groups?.["codes"]?.trim().split(/\s+/u) ?? [
      match.groups?.["warning"] ?? "*",
    ];
    found.push(...rules.map((rule) => inline(file, "native-compiler", rule, lines, index)));
  }
  for (const match of text.matchAll(/_Pragma\s*\(\s*(?!")\w+/gu)) {
    const index = text.slice(0, match.index).split("\n").length - 1;
    found.push(inline(file, "native-compiler", "dynamic-pragma", lines, index));
  }
  return found;
}
