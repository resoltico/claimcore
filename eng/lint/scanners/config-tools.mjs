import { parse } from "smol-toml";
import { strings, table } from "../model.mjs";
import { configOccurrence } from "./config-web.mjs";

/**
 * Everything pyproject.toml tells ruff and mypy to ignore.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanPyproject(file, text) {
  const tool = table(table(parse(text))["tool"]);
  const ruff = table(tool["ruff"]);
  const lint = table(ruff["lint"]);
  const mypy = table(tool["mypy"]);
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  /** @param {string} name @param {string} rule */
  const add = (name, rule) => found.push(configOccurrence(file, name, rule));
  for (const key of ["ignore", "extend-ignore"]) {
    strings(lint[key]).forEach((code) => add("ruff", `${key}:${code}`));
  }
  for (const [glob, codes] of Object.entries(table(lint["per-file-ignores"]))) {
    strings(codes).forEach((code) => add("ruff", `per-file-ignores:${glob}:${code}`));
  }
  for (const [glob, codes] of Object.entries(table(lint["extend-per-file-ignores"]))) {
    strings(codes).forEach((code) => add("ruff", `per-file-ignores:${glob}:${code}`));
  }
  for (const holder of [ruff, lint, table(ruff["format"])]) {
    for (const key of ["exclude", "extend-exclude"]) {
      strings(holder[key]).forEach((entry) => add("ruff", `${key}:${entry}`));
    }
  }
  strings(mypy["exclude"]).forEach((entry) => add("mypy", `exclude:${entry}`));
  strings(mypy["disable_error_code"]).forEach((code) => add("mypy", `disable_error_code:${code}`));
  if (mypy["ignore_missing_imports"] === true) {
    add("mypy", "ignore_missing_imports");
  }
  for (const override of Array.isArray(mypy["overrides"]) ? mypy["overrides"] : []) {
    const entry = table(override);
    const modules = strings(entry["module"]).join(",");
    for (const key of ["ignore_errors", "ignore_missing_imports"]) {
      if (entry[key] === true) {
        add("mypy", `override:${modules}:${key}`);
      }
    }
    strings(entry["disable_error_code"]).forEach((code) =>
      add("mypy", `override:${modules}:disable_error_code:${code}`),
    );
  }
  return found;
}

/**
 * Rules excluded by the PSScriptAnalyzer settings file.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanPowerShellSettings(file, text) {
  const excluded = /ExcludeRules\s*=\s*@\(([^)]*)\)/gisu;
  const rules = [...text.matchAll(excluded)].flatMap((match) =>
    [...(match[1] ?? "").matchAll(/['"]([^'"]+)['"]/gu)].map((rule) => rule[1] ?? ""),
  );
  return rules.map((rule) => configOccurrence(file, "psscriptanalyzer", rule));
}

/**
 * Codes disabled in a shellcheck configuration file.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanShellcheckConfig(file, lines) {
  return lines.flatMap((line) => {
    const match = /^\s*disable\s*=\s*(?<codes>[A-Za-z0-9,\s-]+)/u.exec(line);
    return (match?.groups?.["codes"] ?? "")
      .split(/[\s,]+/u)
      .filter((code) => code !== "")
      .map((code) => configOccurrence(file, "shellcheck", code));
  });
}

/**
 * Diagnostics silenced through .editorconfig and warnings hidden in project files.
 * @param {string} file
 * @param {string} text
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanBuildConfig(file, text) {
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  if (file.endsWith(".editorconfig")) {
    for (const match of text.matchAll(
      /dotnet_diagnostic\.([^.\s]+)\.severity\s*=\s*(?:none|silent)/giu,
    )) {
      found.push(configOccurrence(file, "dotnet-analyzer", match[1] ?? ""));
    }
    return found;
  }
  const property =
    /<(?:NoWarn|WarningsNotAsErrors)\b[^>]*>([^<]*)<|\b(?:NoWarn|WarningsNotAsErrors)\s*=\s*"([^"]*)"/gu;
  for (const match of text.matchAll(property)) {
    for (const code of (match[1] ?? match[2] ?? "")
      .split(/[,;\s]+/u)
      .filter((part) => part !== "" && !part.startsWith("$"))) {
      found.push(configOccurrence(file, "msbuild", /^\d+$/u.test(code) ? `FS${code}` : code));
    }
  }
  return found;
}
