import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createCoverageMap, createFileCoverage } from "@vitest/istanbul-lib-coverage";
import { create, createContext } from "@vitest/istanbul-lib-report";
import SanitizedPlaywrightReporter from "./playwright-reporter.mjs";
import { browserFailureKind } from "./playwright-diagnostics.mjs";
// Runtime loading preserves the engine implementation's own TypeScript project boundary.
const owner = /** @type {unknown} */ (
  await import(new URL("../../eng/ci/run-frontend-reports.mjs", import.meta.url).href)
);
assert.ok(
  typeof owner === "object" &&
    owner !== null &&
    "frontendReports" in owner &&
    typeof owner.frontendReports === "function",
);
const { frontendReports } = /** @type {{frontendReports:(root:string)=>unknown}} */ (owner);

/** @param {string} root @returns {{total:import("@vitest/istanbul-lib-coverage").CoverageSummaryData} & Record<string,import("@vitest/istanbul-lib-coverage").CoverageSummaryData>} */
function measuredSummary(root) {
  const source = join(root, "web/src/a/measured.ts");
  mkdirSync(join(root, "web/src/a"), { recursive: true });
  writeFileSync(source, "export const measured = () => true;\n");
  const location = { start: { line: 1, column: 0 }, end: { line: 1, column: 34 } };
  const file = createFileCoverage({
    path: source,
    statementMap: { 0: location },
    s: { 0: 1 },
    fnMap: { 0: { name: "measured", decl: location, loc: location, line: 1 } },
    f: { 0: 1 },
    branchMap: { 0: { type: "if", loc: location, locations: [location, location], line: 1 } },
    b: { 0: [1, 0] },
  });
  const map = createCoverageMap({});
  map.addFileCoverage(file);
  const second = join(root, "web/src/b/measured.ts");
  mkdirSync(join(root, "web/src/b"), { recursive: true });
  writeFileSync(second, "export const measured = () => true;\n");
  map.addFileCoverage({ ...file.toJSON(), path: second });
  const coverage = join(root, "artifacts/frontend/coverage");
  create("json-summary").execute(createContext({ dir: coverage, coverageMap: map }));
  return JSON.parse(readFileSync(join(coverage, "coverage-summary.json"), "utf8"));
}

/** @param {(root:string,summary:{total:import("@vitest/istanbul-lib-coverage").CoverageSummaryData} & Record<string,import("@vitest/istanbul-lib-coverage").CoverageSummaryData>)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-frontend-report-"));
  try {
    const summary = measuredSummary(root);
    const inventory = readFileSync(
      resolve(import.meta.dirname, "../../tests/inventory/vitest.txt"),
      "utf8",
    )
      .trimEnd()
      .split("\n");
    mkdirSync(join(root, "artifacts/frontend/coverage"), { recursive: true });
    writeFileSync(
      join(root, "artifacts/frontend/vitest-summary.json"),
      JSON.stringify({
        format: "claimcore-vitest-report",
        formatVersion: 1,
        status: "passed",
        totals: { passed: inventory.length, failed: 0, skipped: 0, todo: 0 },
        tests: inventory.map((id) => ({ id, outcome: "passed", durationMs: 1 })),
      }),
    );
    body(root, summary);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

/** @param {string} root @param {unknown} summary */
function admit(root, summary) {
  writeFileSync(
    join(root, "artifacts/frontend/coverage/coverage-summary.json"),
    JSON.stringify(summary),
  );
  const reports = frontendReports(root);
  assert.ok(Array.isArray(reports) && reports.every((report) => typeof report === "string"));
  return reports;
}

test("raw frontend admission accepts measured locked producer summaries with aggregate branchesTrue", () => {
  fixture((root, summary) => {
    assert.deepEqual(summary.total.branchesTrue, { total: 0, covered: 0, skipped: 0, pct: 100 });
    assert.equal(summary.total.branches.total, 4);
    assert.equal(summary.total.branches.covered, 2);
    assert.equal(admit(root, summary).length, 2);
    delete summary.total.branchesTrue;
    assert.equal(admit(root, summary).length, 2);
  });
});

test("raw frontend admission rejects unknown or missing metrics and malformed optional counters", () => {
  fixture((root, summary) => {
    for (const change of [
      { ...summary.total, unknown: summary.total.branchesTrue },
      { ...summary.total, branchesTrue: { ...summary.total.branchesTrue, unknown: 0 } },
      { ...summary.total, branchesTrue: { total: 1, covered: 2, skipped: 0, pct: 200 } },
      { ...summary.total, branchesTrue: { total: 1, covered: 1, skipped: 1, pct: 100 } },
      { ...summary.total, branchesTrue: { total: 2, covered: 1, skipped: 0, pct: 100 } },
    ]) {
      assert.throws(() => admit(root, { ...summary, total: change }));
    }
    const missing = {
      branches: summary.total.branches,
      functions: summary.total.functions,
      statements: summary.total.statements,
    };
    assert.throws(() => admit(root, { ...summary, total: missing }));
  });
});

test("raw frontend admission refuses percentage coercion despite plausible zero counters", () => {
  fixture((root, summary) => {
    for (const pct of [null, [], "", "0", false, "Unknown"]) {
      const branchesTrue = { total: 1, covered: 0, skipped: 0, pct };
      assert.throws(() => admit(root, { ...summary, total: { ...summary.total, branchesTrue } }));
    }
  });
});

test("browser failure classification retains closed kinds and never copies provider payloads", () => {
  const privateValue = "PRIVATE-BROWSER-PAYLOAD";
  const cases = [
    [
      "Protocol error (Network.getResponseBody): No resource with given identifier found",
      "response-body-unavailable",
    ],
    [
      "Protocol error (Network.getResponseBody): Request content was evicted from inspector cache",
      "response-body-unavailable",
    ],
    ["SyntaxError: Unexpected end of JSON input", "json-parse"],
    ["SyntaxError: Unexpected token '<', is not valid JSON", "json-parse"],
    ["response.body: Target page, context or browser has been closed", "cancelled-or-closed"],
    ["AbortError: The operation was aborted", "cancelled-or-closed"],
    ["page.waitForResponse: Timeout 10000ms exceeded", "timeout"],
    ["Error: expect(locator).toBeVisible() failed", "assertion"],
    ["Unrecognized engine failure", "unknown"],
  ];
  for (const [message, expected] of cases) {
    const actual = browserFailureKind({ message: `${message} ${privateValue}` });
    assert.equal(actual, expected);
    assert.ok(!actual.includes(privateValue));
  }
  for (const error of [null, undefined, privateValue, { message: 3 }, { value: privateValue }]) {
    assert.equal(browserFailureKind(error), "unknown");
  }
  assert.equal(
    browserFailureKind({ message: `${"x".repeat(8192)}Unexpected end of JSON input` }),
    "unknown",
  );
});

test("browser reporter preserves original step failure independently of final cleanup errors", () => {
  const reporter = new SanitizedPlaywrightReporter();
  const source = resolve(import.meta.dirname, "../e2e/case-workflow.ts");
  const testCase = /** @type {Parameters<SanitizedPlaywrightReporter["onTestEnd"]>[0]} */ (
    /** @type {unknown} */ ({ title: "synthetic browser diagnostic" })
  );
  const result = /** @type {Parameters<SanitizedPlaywrightReporter["onTestEnd"]>[1]} */ (
    /** @type {unknown} */ ({
      status: "failed",
      duration: 1,
      attachments: [],
      errors: [
        { message: "Target closed PRIVATE-CLEANUP-PAYLOAD" },
        ...Array.from({ length: 10 }, () => ({ message: "Unrecognized PRIVATE-PAYLOAD" })),
      ],
    })
  );
  const step = /** @type {Parameters<SanitizedPlaywrightReporter["onStepEnd"]>[2]} */ (
    /** @type {unknown} */ ({
      category: "pw:api",
      title: "PRIVATE-SELECTOR",
      location: { file: source, line: 59, column: 1 },
      error: {
        message:
          "Protocol error (Network.getResponseBody): No resource with given identifier found PRIVATE-RESPONSE-PAYLOAD",
      },
    })
  );
  reporter.onTestBegin(testCase, result);
  reporter.onStepEnd(testCase, result, step);
  reporter.onStepEnd(testCase, result, {
    ...step,
    error: { message: "Target closed PRIVATE-CLEANUP-PAYLOAD" },
  });
  reporter.onTestEnd(testCase, result);
  assert.equal(
    reporter.stepDiagnostics.get(result)?.snapshot()?.errorKind,
    "response-body-unavailable",
  );
  const [diagnostic] = reporter.diagnosticTests;
  assert.ok(typeof diagnostic === "object" && diagnostic !== null && "errorKinds" in diagnostic);
  assert.deepEqual(diagnostic.errorKinds, ["cancelled-or-closed", ...Array(7).fill("unknown")]);
  assert.ok(!JSON.stringify(diagnostic).includes("PRIVATE"));
});
