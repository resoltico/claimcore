import { parseJsonc } from "../jsonc.mjs";
import { isObject } from "../model.mjs";

/**
 * @param {string} file
 * @param {string} tool
 * @param {string} rule
 * @returns {import("../model.mjs").Occurrence}
 */
export function configOccurrence(file, tool, rule) {
  return { file, tool, rule, kind: "config", line: 1, id: null };
}

/**
 * @param {string} file
 * @param {unknown} rules
 * @returns {import("../model.mjs").Occurrence[]}
 */
function disabledRules(file, rules) {
  if (!isObject(rules)) return [];
  return Object.entries(rules)
    .filter(([, setting]) => {
      const level = Array.isArray(setting) ? setting[0] : setting;
      return level === "off" || level === 0 || level === "allow";
    })
    .map(([name]) => configOccurrence(file, "oxlint", name));
}

/**
 * Rules switched off and files ignored by an oxlint configuration.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanOxlintConfig(file, text) {
  const config = parseJsonc(text);
  if (!isObject(config)) return [];
  const patterns = Array.isArray(config["ignorePatterns"]) ? config["ignorePatterns"] : [];
  const overrides = Array.isArray(config["overrides"]) ? config["overrides"] : [];
  return [
    ...disabledRules(file, config["rules"]),
    ...patterns.map((pattern) =>
      configOccurrence(file, "oxlint", `ignorePatterns:${String(pattern)}`),
    ),
    ...overrides.flatMap((entry) => (isObject(entry) ? disabledRules(file, entry["rules"]) : [])),
  ];
}

/**
 * Files a TypeScript project leaves out.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanTypeScriptConfig(file, text) {
  const config = parseJsonc(text);
  const exclude = isObject(config) && Array.isArray(config["exclude"]) ? config["exclude"] : [];
  return exclude.map((pattern) => configOccurrence(file, "tsc", `exclude:${String(pattern)}`));
}

/**
 * Anything knip is told to ignore.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanKnipConfig(file, text) {
  const config = parseJsonc(text);
  if (!isObject(config)) return [];
  return Object.entries(config)
    .filter(([key]) => key.startsWith("ignore"))
    .flatMap(([key, value]) =>
      (Array.isArray(value) ? value : [key]).map((item) =>
        configOccurrence(file, "knip", `${key}:${String(item)}`),
      ),
    );
}

/**
 * Non-comment lines of an ignore file.
 * @param {string} file
 * @param {string[]} lines
 * @param {string} tool
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanIgnoreFile(file, lines, tool) {
  return lines
    .map((line) => line.trim())
    .filter((line) => line !== "" && !line.startsWith("#"))
    .map((line) => configOccurrence(file, tool, `ignore-file:${line}`));
}

/**
 * Stylelint rules set to null and ignore options.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanStylelintConfig(file, lines) {
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  for (const line of lines) {
    const off = /["'](?<rule>[^"']+)["']\s*:\s*null\b/.exec(line);
    if (off) found.push(configOccurrence(file, "stylelint", off.groups?.["rule"] ?? ""));
    if (/\b(?:ignoreFiles|ignoreDisables)\b/.test(line))
      found.push(configOccurrence(file, "stylelint", "ignore-options"));
  }
  return found;
}

/**
 * Coverage excludes in the Vite or package configuration.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanCoverageConfig(file, lines) {
  const pattern =
    /(?:coveragePathIgnorePatterns|coverage.*--exclude|(?:istanbul|c8).*--exclude|^\s*["']?exclude(?:AfterRemap)?["']?\s*:)/i;
  return lines
    .filter((line) => pattern.test(line))
    .map(() => configOccurrence(file, "coverage", "coverage-config-exclude"));
}
