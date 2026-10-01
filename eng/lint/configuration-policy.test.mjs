import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { checkOxlintLimits } from "./policies/oxlint.mjs";
import { Report } from "./model.mjs";
import { withTree } from "./test-support.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const policy = () => JSON.parse(readFileSync(`${root}/config/oxlint.json`, "utf8"));

/** @param {Record<string, unknown>} child @param {Record<string, unknown>} [base] */
function errors(child, base = policy()) {
  return withTree(
    {
      "config/oxlint.json": JSON.stringify(base),
      "eng/.oxlintrc.json": JSON.stringify({ extends: ["../config/oxlint.json"], ...child }),
    },
    (directory) => {
      const report = new Report();
      checkOxlintLimits(directory, "eng/.oxlintrc.json", report);
      return report.errors;
    },
  );
}

test("native inherited lint policy is complete without duplicate package rules", () => {
  assert.deepEqual(errors({}), []);
});

test("scope overrides cannot replace complexity, severity or physical measurement policy", () => {
  for (const rules of [
    { complexity: ["error", { max: 999 }] },
    { "max-lines-per-function": ["error", { max: 999 }] },
    { complexity: ["off", { max: 12 }] },
    { "max-lines": ["error", { max: 300, skipComments: true }] },
    { "max-lines-per-function": ["error", { max: 50, IIFEs: false }] },
    { "max-params": ["error", { max: -1 }] },
  ]) {
    assert.ok(errors({ overrides: [{ files: ["**/*.mjs"], rules }] }).length > 0);
  }
  assert.ok(errors({ options: { reportUnusedDisableDirectives: "off" } }).length > 0);
  assert.ok(
    errors({ overrides: [{ files: ["**/*.mjs"], categories: { correctness: "off" } }] }).length > 0,
  );
});

test("missing, cyclic and escaping inheritance are refusals", () => {
  for (const parent of ["missing.json", "../eng/.oxlintrc.json", "../../outside.json"]) {
    assert.match(errors({ extends: [parent] }).join("\n"), /inheritance/u);
  }
});
