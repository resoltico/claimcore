import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { findings } from "./test-support.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const policy = readFileSync(join(root, "web/stylelint.config.mjs"), "utf8");

test("CSS nesting, selector specificity and compound ceilings cannot be weakened or hidden", () => {
  assert.deepEqual(findings({ "web/stylelint.config.mjs": policy }, []), []);
  for (const source of [
    policy.replace('"max-nesting-depth": 2', '"max-nesting-depth": 20'),
    policy.replace('"selector-max-specificity": "0,3,0"', '"selector-max-specificity": "10,10,10"'),
    policy.replace('"selector-max-compound-selectors": 3', '"selector-max-compound-selectors": 30'),
    policy.replace("rules: {", "rules: { ...hidden,"),
    policy.replace("rules: {", "overrides: [], rules: {"),
    policy.replace("rules: {", 'rules: { "max-nesting-depth": 20,'),
  ]) {
    assert.match(
      findings({ "web/stylelint.config.mjs": source }, []).join("\n"),
      /stylelint\.config/u,
    );
  }
});
