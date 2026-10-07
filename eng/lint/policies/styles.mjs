// Stylelint's complexity ceilings are literal policy, never executable configuration decisions.
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { parseSync } from "oxc-parser";
import { table } from "../model.mjs";

/** @param {Record<string,unknown>} property @param {unknown} name @param {Record<string,unknown>} result */
function plainProperty(property, name, result) {
  return (
    property["type"] === "Property" &&
    !property["computed"] &&
    !property["method"] &&
    property["kind"] === "init" &&
    typeof name === "string" &&
    !Object.hasOwn(result, name)
  );
}

/** @param {unknown} node @returns {unknown} */
function literal(node) {
  const value = table(node);
  if (value["type"] === "Literal") {
    return value["value"];
  }
  if (value["type"] === "ArrayExpression" && Array.isArray(value["elements"])) {
    return value["elements"].map(literal);
  }
  if (value["type"] !== "ObjectExpression" || !Array.isArray(value["properties"])) {
    throw new Error("Style policy must use static literals.");
  }
  /** @type {Record<string,unknown>} */
  const result = {};
  for (const item of value["properties"]) {
    const property = table(item);
    const key = table(property["key"]);
    const name = key["type"] === "Identifier" ? key["name"] : key["value"];
    if (!plainProperty(property, name, result)) {
      throw new Error("Style policy cannot hide or override a declaration.");
    }
    result[String(name)] = literal(property["value"]);
  }
  return result;
}

/** @param {string} root @param {import("../model.mjs").Report} report */
export function checkStyleLimits(root, report) {
  const path = "web/stylelint.config.mjs";
  if (!existsSync(join(root, path))) {
    return;
  }
  try {
    const { program, errors } = parseSync(path, readFileSync(join(root, path), "utf8"));
    if (
      errors.length ||
      program.body.length !== 1 ||
      program.body[0]?.type !== "ExportDefaultDeclaration"
    ) {
      throw new Error("Style policy must export one static object.");
    }
    const config = table(literal(program.body[0].declaration));
    const rules = table(config["rules"]);
    for (const [name, expected] of Object.entries({
      "max-nesting-depth": 2,
      "selector-max-specificity": "0,3,0",
      "selector-max-compound-selectors": 3,
    })) {
      if (rules[name] !== expected) {
        report.add(`${path} must retain '${name}' = ${String(expected)}.`);
      }
    }
    if (Object.hasOwn(config, "overrides")) {
      throw new Error("Style policy overrides need explicit ceiling validation.");
    }
  } catch {
    report.add(`${path} must be an unambiguous static style-complexity policy.`);
  }
}
