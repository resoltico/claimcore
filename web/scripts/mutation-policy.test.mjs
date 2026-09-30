import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { verifyMutationReport } from "./check-mutation-report.mjs";

const target = "src/domain/operationReducer.ts";
const evidence = () => ({
  schemaVersion: "1.0",
  framework: { name: "StrykerJS", version: "10.0.0" },
  thresholds: { high: 92, low: 92, break: 92 },
  files: { [target]: { source: "synthetic", mutants: [{ status: "Killed" }] } },
  testFiles: { "tests/synthetic.test.ts": {} },
});

test("mutation evidence accepts one exact reviewed target", () => {
  assert.equal(verifyMutationReport(evidence(), "synthetic", "10.0.0").score, 100);
});

test("mutation evidence rejects target or toolchain substitution", () => {
  const changed = evidence();
  changed.files = { "src/other.ts": changed.files[target] };
  assert.throws(() => verifyMutationReport(changed, "synthetic", "10.0.0"));
  assert.throws(() => verifyMutationReport(evidence(), "changed", "10.0.0"));
  assert.throws(() => verifyMutationReport(evidence(), "synthetic", "11.0.0"));
});

test("mutation evidence rejects empty, ignored, or below-floor mutants", () => {
  const empty = evidence();
  empty.files[target].mutants = [];
  assert.throws(() => verifyMutationReport(empty, "synthetic", "10.0.0"));
  const ignored = evidence();
  ignored.files[target].mutants = [{ status: "Ignored" }];
  assert.throws(() => verifyMutationReport(ignored, "synthetic", "10.0.0"));
  const below = evidence();
  below.files[target].mutants.push({ status: "Survived" });
  assert.throws(() => verifyMutationReport(below, "synthetic", "10.0.0"));
});

test("mutation testing never needs the TypeScript compiler API that TypeScript 7 does not ship", () => {
  const config = JSON.parse(
    readFileSync(new URL("../stryker.config.json", import.meta.url), "utf8"),
  );
  // StrykerJS rewrites an existing tsconfig through that API; Vitest transpiles without one, so the
  // named file must stay absent for the sandbox rewrite to be skipped.
  assert.equal(typeof config.tsconfigFile, "string");
  assert.equal(existsSync(new URL(`../${config.tsconfigFile}`, import.meta.url)), false);
  assert.notEqual(config.inPlace, true);
});
