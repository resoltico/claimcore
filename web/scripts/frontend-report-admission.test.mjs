import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createCoverageMap, createFileCoverage } from "@vitest/istanbul-lib-coverage";
import { create, createContext } from "@vitest/istanbul-lib-report";
import { frontendReports } from "../../eng/ci/run-frontend-reports.mjs";

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
  return frontendReports(root);
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
