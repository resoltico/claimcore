import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { targets, verifyMutationReport } from "./check-mutation-report.mjs";

const target = targets[0] ?? "";
const evidence = () => ({
  schemaVersion: "1.0",
  framework: { name: "StrykerJS", version: "10.0.0" },
  thresholds: { high: 92, low: 92, break: 92 },
  files: Object.fromEntries(
    targets.map((name) => [name, { source: "synthetic", mutants: [{ status: "Killed" }] }]),
  ),
  testFiles: { "tests/synthetic.test.ts": {} },
});
/** @param {import("./tooling-types.mjs").MutationReport} report */
const targetFile = (report) => {
  const file = report.files[target];
  assert.ok(file);
  return file;
};
const sources = () => Object.fromEntries(targets.map((name) => [name, "synthetic"]));

test("mutation evidence accepts exactly the reviewed targets", () => {
  assert.equal(verifyMutationReport(evidence(), sources(), "10.0.0").score, 100);
});

test("mutation evidence rejects target or toolchain substitution", () => {
  const changed = evidence();
  changed.files = { ...changed.files, "src/other.ts": targetFile(changed) };
  assert.throws(() => verifyMutationReport(changed, sources(), "10.0.0"));
  const missing = evidence();
  delete missing.files[target];
  assert.throws(() => verifyMutationReport(missing, sources(), "10.0.0"));
  assert.throws(() =>
    verifyMutationReport(evidence(), { ...sources(), [target]: "changed" }, "10.0.0"),
  );
  assert.throws(() => verifyMutationReport(evidence(), sources(), "11.0.0"));
});

test("mutation evidence rejects empty, ignored, or below-floor mutants", () => {
  const empty = evidence();
  for (const name of targets) {
    const file = empty.files[name];
    assert.ok(file);
    file.mutants = [];
  }
  assert.throws(() => verifyMutationReport(empty, sources(), "10.0.0"));
  const ignored = evidence();
  targetFile(ignored).mutants = [{ status: "Ignored" }];
  assert.throws(() => verifyMutationReport(ignored, sources(), "10.0.0"));
  const below = evidence();
  targetFile(below).mutants.push({ status: "Survived" }, { status: "Survived" });
  assert.throws(() => verifyMutationReport(below, sources(), "10.0.0"));
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
