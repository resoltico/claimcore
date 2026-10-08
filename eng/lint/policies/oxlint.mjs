// Validate inherited policy declarations and scope overrides, not only a package's top level.
import { readFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { parseJsonc } from "../jsonc.mjs";
import { isObject, table } from "../model.mjs";
import { resolveSourceFile } from "../../ci/repository-path.mjs";

const categories = ["correctness", "suspicious", "pedantic", "perf", "style"];
const ceilings = {
  complexity: 12,
  "max-lines": 300,
  "max-lines-per-function": 50,
  "max-params": 5,
  "max-statements": 50,
};

/** @param {string} root @param {string} path @param {Set<string>} [visiting] @returns {Record<string, unknown>[]} */
function declarations(root, path, visiting = new Set()) {
  if (visiting.has(path)) {
    throw new Error("Oxlint configuration inheritance contains a cycle.");
  }
  visiting.add(path);
  const value = parseJsonc(readFileSync(resolveSourceFile(resolve(root), path), "utf8"));
  if (!isObject(value)) {
    throw new Error("Oxlint configuration must be an object.");
  }
  const parents = value["extends"] ?? [];
  if (!Array.isArray(parents) || !parents.every((parent) => typeof parent === "string")) {
    throw new Error("Oxlint inheritance must name repository-local configuration files.");
  }
  const inherited = parents.flatMap((parent) =>
    declarations(
      root,
      relative(root, resolve(root, dirname(path), parent))
        .split("\\")
        .join("/"),
      visiting,
    ),
  );
  visiting.delete(path);
  return [...inherited, value];
}

/** @param {unknown} setting @param {string} rule @param {number} maximum @returns {boolean} */
function validLimit(setting, rule, maximum) {
  if (!Array.isArray(setting) || setting[0] !== "error") {
    return false;
  }
  const [, option] = setting;
  const value = typeof option === "number" ? option : table(option)["max"];
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > maximum) {
    return false;
  }
  const options = table(option);
  if (
    rule.startsWith("max-lines") &&
    (options["skipBlankLines"] === true || options["skipComments"] === true)
  ) {
    return false;
  }
  return rule !== "max-lines-per-function" || options["IIFEs"] === true;
}

/** @param {Record<string, unknown>} scope @param {string} label @param {import("../model.mjs").Report} report @param {boolean} complete */
function checkRuleLimits(scope, label, report, complete) {
  const rules = table(scope["rules"]);
  for (const [name, maximum] of Object.entries(ceilings)) {
    for (const key of [name, `eslint/${name}`]) {
      if ((complete && key === name) || rules[key] !== undefined) {
        if (!validLimit(rules[key], name, maximum)) {
          report.add(
            `${label} must set '${key}' to error with a positive limit at most ${maximum}, without measurement exclusions.`,
          );
        }
      }
    }
  }
}

/** @param {Record<string, unknown>} scope @param {string} label @param {import("../model.mjs").Report} report @param {boolean} complete */
function checkScope(scope, label, report, complete) {
  checkRuleLimits(scope, label, report, complete);
  for (const name of categories) {
    const level = table(scope["categories"])[name];
    if ((complete || level !== undefined) && level !== "error") {
      report.add(`${label} must set the '${name}' rule category to "error".`);
    }
  }
  const options = table(scope["options"]);
  for (const [name, expected] of Object.entries({
    denyWarnings: true,
    reportUnusedDisableDirectives: "error",
  })) {
    if ((complete || options[name] !== undefined) && options[name] !== expected) {
      report.add(`${label} must set options.${name} to ${String(expected)}.`);
    }
  }
}

/** @param {string} root @param {string} path @param {import("../model.mjs").Report} report */
export function checkOxlintLimits(root, path, report) {
  try {
    const chain = declarations(root, path);
    const effective = { rules: {}, categories: {}, options: {} };
    for (const config of chain) {
      for (const key of /** @type {const} */ (["rules", "categories", "options"])) {
        Object.assign(effective[key], table(config[key]));
      }
      checkScope(config, path, report, false);
      for (const override of Array.isArray(config["overrides"]) ? config["overrides"] : []) {
        checkScope(table(override), `${path} override`, report, false);
      }
    }
    checkScope(effective, path, report, true);
  } catch {
    report.add(`${path} has unsafe, missing or malformed Oxlint inheritance.`);
  }
}
