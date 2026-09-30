import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { parse } from "smol-toml";
import { parseJsonc } from "../jsonc.mjs";
import { table } from "../model.mjs";

/** @type {Array<[string, string, number]>} */
const fsharpLimits = [
  ["cyclomaticComplexity", "maxComplexity", 12],
  ["maxLinesInFile", "maxLinesInFile", 300],
  ["maxLinesInModule", "maxLines", 300],
  ["maxLinesInFunction", "maxLines", 50],
  ["maxLinesInLambdaFunction", "maxLines", 50],
  ["maxLinesInMatchLambdaFunction", "maxLines", 50],
  ["maxLinesInValue", "maxLines", 50],
  ["maxLinesInMember", "maxLines", 50],
  ["maxLinesInConstructor", "maxLines", 50],
  ["maxLinesInProperty", "maxLines", 50],
];
/** @type {Array<[string, number]>} */
const pythonPylintLimits = [
  ["max-args", 8],
  ["max-positional-args", 5],
  ["max-statements", 50],
];
const oxlintCategories = ["correctness", "suspicious", "pedantic", "perf", "style"];
const nestedExclusion = /(ignore|exclude|suppress|allowed)/iu;

/**
 * @param {string} root
 * @param {string} path
 * @returns {unknown}
 */
function readJson(root, path) {
  return existsSync(join(root, path)) ? parseJsonc(readFileSync(join(root, path), "utf8")) : null;
}

/**
 * @param {Record<string, unknown>} config
 * @param {import("../model.mjs").Report} report
 */
function checkFSharpLintExclusions(config, report) {
  for (const [name, value] of Object.entries(config)) {
    const rule = table(value);
    if (rule["enabled"] === false) {
      report.add(`config/fsharplint.json disables '${name}' outside the registry.`);
    }
    if (name !== "ignoreFiles" && nestedExclusion.test(name) && Object.keys(rule).length > 0) {
      report.add(`config/fsharplint.json configures '${name}' outside the central registry.`);
    }
  }
}

/**
 * The F# linter may not be weakened outside the registry.
 * @param {string} root
 * @param {string[]} generated Registered generated-output paths.
 * @param {import("../model.mjs").Report} report
 */
export function checkFSharpLint(root, generated, report) {
  const config = table(readJson(root, "config/fsharplint.json"));
  if (Object.keys(config).length === 0) {
    return;
  }
  const ignored = Array.isArray(config["ignoreFiles"]) ? config["ignoreFiles"].map(String) : [];
  if (JSON.stringify([...ignored].sort()) !== JSON.stringify([...generated].sort())) {
    report.add(
      "config/fsharplint.json ignoreFiles must exactly match the generated exclusions in config/lint-exceptions.json.",
    );
  }
  checkFSharpLintExclusions(config, report);
  for (const [name, property, maximum] of fsharpLimits) {
    const rule = table(config[name]);
    const value = table(rule["config"])[property];
    if (rule["enabled"] !== true) {
      report.add(`config/fsharplint.json must keep '${name}' enabled.`);
    } else if (typeof value !== "number" || value > maximum) {
      report.add(
        `config/fsharplint.json weakens '${name}' to ${String(value)}; the maximum is ${maximum}.`,
      );
    }
  }
}

/**
 * The size and complexity ceilings of the TypeScript linter are fixed.
 * @param {string} root
 * @param {string} path
 * @param {import("../model.mjs").Report} report
 */
export function checkOxlintLimits(root, path, report) {
  const config = table(readJson(root, path));
  const categories = table(config["categories"]);
  for (const category of oxlintCategories) {
    if (categories[category] !== "error") {
      report.add(`${path} must set the '${category}' rule category to "error".`);
    }
  }
  if (table(config["options"])["denyWarnings"] !== true) {
    report.add(`${path} must set options.denyWarnings to true.`);
  }
  const rules = table(config["rules"]);
  /** @type {Array<[string, string, number]>} */
  const limits = [
    ["complexity", "max", 12],
    ["max-lines", "max", 300],
    ["max-lines-per-function", "max", 50],
  ];
  for (const [name, property, maximum] of limits) {
    const setting = rules[name];
    const options = Array.isArray(setting) ? table(setting[1]) : {};
    const value = options[property];
    if (typeof value !== "number" || value > maximum) {
      report.add(`${path} must set '${name}' with ${property} at most ${maximum}.`);
    }
  }
}

/**
 * ruff runs every rule and mypy runs strict; both are fixed.
 * @param {string} root
 * @param {import("../model.mjs").Report} report
 */
export function checkPythonPolicy(root, report) {
  const path = join(root, "pyproject.toml");
  if (!existsSync(path)) {
    return;
  }
  const tool = table(table(parse(readFileSync(path, "utf8")))["tool"]);
  const lint = table(table(tool["ruff"])["lint"]);
  const select = Array.isArray(lint["select"]) ? lint["select"].map(String) : [];
  if (select.length !== 1 || select[0] !== "ALL") {
    report.add('pyproject.toml must select exactly ["ALL"] ruff rules; narrow nothing.');
  }
  if (table(tool["mypy"])["strict"] !== true) {
    report.add("pyproject.toml must run mypy with strict = true.");
  }
  const complexity = table(lint["mccabe"])["max-complexity"];
  if (typeof complexity !== "number" || complexity > 12) {
    report.add("pyproject.toml must cap ruff McCabe complexity at 12 or less.");
  }
  const pylint = table(lint["pylint"]);
  for (const [name, maximum] of pythonPylintLimits) {
    const value = pylint[name];
    if (typeof value !== "number" || value > maximum) {
      report.add(`pyproject.toml must set ruff pylint '${name}' to at most ${maximum}.`);
    }
  }
}
