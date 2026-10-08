import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { targets, verifyMutationReport } from "./check-mutation-report.mjs";

const target = targets[0] ?? "";
/** @returns {import("./tooling-types.mjs").MutationReport} */
const evidence = () => ({
  schemaVersion: "1.0",
  framework: { name: "StrykerJS", version: "10.0.0" },
  thresholds: { high: 92, low: 92, break: 92 },
  config: {
    mutate: targets,
    testRunner: "vitest",
    vitest: { configFile: "vite.config.ts", related: true },
    ignorePatterns: ["artifacts/**"],
    coverageAnalysis: "perTest",
    mutator: { plugins: null, excludedMutations: [] },
    plugins: ["@stryker-mutator/*"],
    appendPlugins: [],
    ignorers: [],
    checkers: [],
    ignoreStatic: false,
    incremental: false,
    dryRunOnly: false,
    inPlace: false,
    testFiles: [],
    testRunnerNodeArgs: [],
  },
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

test("mutation evidence rejects focused, cached, or custom producer profiles despite matching sources", () => {
  const overrides = [
    { mutate: targets.map((name) => `${name}:1:1`) },
    { ignorePatterns: ["artifacts/**", "src/domain/**"] },
    { mutator: { plugins: null, excludedMutations: ["StringLiteral"] } },
    { mutator: { plugins: ["custom"], excludedMutations: [] } },
    { plugins: ["custom"] },
    { appendPlugins: ["custom"] },
    { ignorers: ["custom"] },
    { checkers: ["custom"] },
    { ignoreStatic: true },
    { incremental: true },
    { dryRunOnly: true },
    { inPlace: true },
    { testFiles: ["tests/one.test.ts"] },
    { testRunnerNodeArgs: ["--require", "custom-loader.cjs"] },
    { vitest: { configFile: "vite.config.ts", related: true, dir: "tests/subset" } },
    { vitest: { configFile: "focused.config.ts", related: true } },
    { coverageAnalysis: "off" },
  ];
  for (const override of overrides) {
    const report = evidence();
    report.config = { ...report.config, ...override };
    assert.throws(() => verifyMutationReport(report, sources(), "10.0.0"));
  }
  const missing = JSON.parse(JSON.stringify(evidence()));
  delete missing.config;
  assert.throws(() => verifyMutationReport(missing, sources(), "10.0.0"));
});

test("mutation profile binds substantive settings without machine paths or execution timing", () => {
  const report = evidence();
  report.config = {
    ...report.config,
    concurrency: 1,
    timeoutMS: 5000,
    incrementalFile: "/machine/cache",
  };
  assert.equal(verifyMutationReport(report, sources(), "10.0.0").score, 100);
});

test("mutation evidence rejects empty, incomplete, ignored, or below-floor mutants", () => {
  const empty = evidence();
  for (const name of targets) {
    const file = empty.files[name];
    assert.ok(file);
    file.mutants = [];
  }
  assert.throws(() => verifyMutationReport(empty, sources(), "10.0.0"));
  for (const status of [
    "Ignored",
    "NoCoverage",
    "CompileError",
    "RuntimeError",
    "Timeout",
    "Pending",
  ]) {
    const incomplete = evidence();
    targetFile(incomplete).mutants = [{ status }];
    assert.throws(() => verifyMutationReport(incomplete, sources(), "10.0.0"));
  }
  const below = evidence();
  targetFile(below).mutants.push({ status: "Survived" }, { status: "Survived" });
  assert.throws(() => verifyMutationReport(below, sources(), "10.0.0"));
});

test("mutation evidence refuses Vitest test and hook deadlines reported as killed mutants", () => {
  for (const statusReason of [
    "Test timed out in 5000ms.\nPinned Vitest guidance.",
    "Hook timed out in 10000ms.\nPinned Vitest guidance.",
    "Test timed out in 5000ms while waiting for a traced operation.\nPinned Vitest guidance.",
    'The setup phase of "aroundEach" hook timed out after 10000ms.',
    'The teardown phase of "aroundAll" hook timed out after 10000ms.',
    "Error: Test timed out in 5000ms.",
  ]) {
    const report = evidence();
    targetFile(report).mutants = [{ status: "Killed", statusReason }];
    assert.throws(() => verifyMutationReport(report, sources(), "10.0.0"));
  }
});

test("mutation evidence accepts ordinary assertions mentioning timeout and refuses malformed reasons", () => {
  const report = evidence();
  for (const statusReason of [
    "AssertionError: expected the timeout outcome to have a different label",
    "AssertionError: expected 'Test timed out in 5000ms.' in the displayed error label",
    "ordinary assertion\nTest timed out in 5000ms.\ncustom expectation",
  ]) {
    targetFile(report).mutants = [{ status: "Killed", statusReason }];
    assert.equal(verifyMutationReport(report, sources(), "10.0.0").score, 100);
  }
  const malformed = JSON.parse(JSON.stringify(report));
  malformed.files[target].mutants[0].statusReason = 5000;
  assert.throws(() => verifyMutationReport(malformed, sources(), "10.0.0"));
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
