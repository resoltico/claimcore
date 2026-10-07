import assert from "node:assert/strict";
import { existsSync, lstatSync, readFileSync } from "node:fs";
import { isAbsolute, join, relative } from "node:path";
import { verifyVitest, verifyBrowser, engines } from "./suites/frontend.mjs";
import { checkBrowserCoverage } from "./coverage-policy/policy.mjs";
import { assertNoLinkAbove } from "./scan/files.mjs";
import { targets, verifyMutationReport } from "../../web/scripts/check-mutation-report.mjs";
/** @param {string} path */
function safeFile(path) {
  assertNoLinkAbove(path);
  assert.ok(lstatSync(path).isFile());
}
/** @param {string} path */
function json(path) {
  safeFile(path);
  return JSON.parse(readFileSync(path, "utf8"));
}
/** @param {string} root @param {string} path */
// These are raw counter/shape admissions, not another coverage-floor or complete-scope policy.
// The executed Vite gate owns thresholds and inclusion; its exact config is retained with source.
function coverageSummary(root, path) {
  const summary =
    /** @type {Record<string,Record<string,{total:number,covered:number,skipped:number,pct:number|string}>>} */ (
      json(path)
    );
  assert.ok(Object.keys(summary).length > 1 && summary["total"]);
  for (const [file, metrics] of Object.entries(summary)) {
    if (file !== "total") {
      const inside = relative(join(root, "web/src"), file);
      assert.ok(!isAbsolute(inside) && inside !== ".." && !inside.startsWith("../"));
      safeFile(file);
    }
    assert.deepEqual(Object.keys(metrics).sort(), ["branches", "functions", "lines", "statements"]);
    for (const counts of Object.values(metrics)) {
      assert.deepEqual(Object.keys(counts).sort(), ["covered", "pct", "skipped", "total"]);
      assert.ok(
        [counts.total, counts.covered, counts.skipped].every(
          (count) => Number.isSafeInteger(count) && count >= 0,
        ),
      );
      assert.ok(counts.covered <= counts.total && counts.skipped === 0);
      const measured = counts.total === 0 ? 100 : (100 * counts.covered) / counts.total;
      assert.ok(Math.abs(Number(counts.pct) - measured) < 0.011);
    }
  }
}
/** @param {string} root */
export function frontendReports(root) {
  const admitted = [];
  const vitest = join(root, "artifacts/frontend/vitest-summary.json");
  if (existsSync(vitest)) {
    safeFile(vitest);
    verifyVitest(root);
    admitted.push(vitest);
    const coverage = join(root, "artifacts/frontend/coverage/coverage-summary.json");
    coverageSummary(root, coverage);
    admitted.push(coverage);
  }
  for (const engine of engines) {
    const report = join(root, `artifacts/browser/${engine}.json`);
    if (existsSync(report)) {
      safeFile(report);
      verifyBrowser(engine, root);
      admitted.push(report);
    }
    const coverage = join(
      root,
      `artifacts/coverage/input/browser/${engine}.coverage.cobertura.e2e.xml`,
    );
    if (existsSync(coverage)) {
      safeFile(coverage);
      checkBrowserCoverage(coverage);
      admitted.push(coverage);
    }
  }
  const mutation = join(root, "web/artifacts/stryker/domain-mutation.json");
  if (existsSync(mutation)) {
    const sources = Object.fromEntries(
      targets.map((target) => [target, readFileSync(join(root, "web", target), "utf8")]),
    );
    verifyMutationReport(
      json(mutation),
      sources,
      json(join(root, "web/package.json")).devDependencies["@stryker-mutator/core"],
    );
    admitted.push(mutation);
  }
  return admitted;
}
