import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { verifyBrowser, verifyVitest } from "./frontend.mjs";
import { parseInventory } from "./inventory.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const inventory = (/** @type {string} */ name) =>
  parseInventory(readFileSync(join(root, "tests/inventory", `${name}.txt`), "utf8"));

/**
 * @param {string[]} names
 * @returns {object[]}
 */
const passed = (names) => names.map((id) => ({ id, outcome: "passed", durationMs: 1 }));

/** @param {string[]} names @returns {object} */
const vitest = (names) => ({
  format: "claimcore-vitest-report",
  formatVersion: 1,
  status: "passed",
  totals: { passed: names.length, failed: 0, skipped: 0, todo: 0 },
  tests: passed(names),
});

/** @param {string[]} names @param {string} scope @returns {object} */
const browser = (names, scope) => ({
  format: "claimcore-playwright-report",
  formatVersion: 1,
  scope,
  status: "passed",
  expected: names.length,
  totals: { passed: names.length, failed: 0, skipped: 0, timedOut: 0, interrupted: 0 },
  tests: passed(names),
  failureLines: [],
  failureCodes: [],
});

/**
 * @param {string} relative
 * @param {object} report
 * @param {(base: string) => void} check
 */
function withReport(relative, report, check) {
  const base = mkdtempSync(join(tmpdir(), "claimcore-frontend-report-"));
  try {
    mkdirSync(dirname(join(base, relative)), { recursive: true });
    writeFileSync(join(base, relative), JSON.stringify(report));
    check(base);
  } finally {
    rmSync(base, { recursive: true, force: true });
  }
}

test("a complete passing vitest report matches the inventory", () => {
  withReport("artifacts/frontend/vitest-summary.json", vitest(inventory("vitest")), (base) =>
    verifyVitest(base),
  );
});

test("vitest reports with a missing, extra, duplicated or failing test are refused", () => {
  const names = inventory("vitest");
  const reports = [
    vitest(names.slice(1)),
    vitest([...names, "an extra test"]),
    { ...vitest(names), tests: passed([...names.slice(1), names[1] ?? ""]) },
    { ...vitest(names), totals: { passed: names.length, failed: 1, skipped: 0, todo: 0 } },
    { ...vitest(names), status: "failed" },
    {
      ...vitest(names),
      tests: [{ id: names[0], outcome: "failed", durationMs: 1 }, ...passed(names.slice(1))],
    },
  ];
  for (const report of reports) {
    withReport("artifacts/frontend/vitest-summary.json", report, (base) =>
      assert.throws(() => verifyVitest(base), /./u),
    );
  }
});

test("browser reports must name their engine and carry no failures", () => {
  const names = inventory("browser");
  withReport("artifacts/browser/webkit.json", browser(names, "webkit"), (base) =>
    verifyBrowser("webkit", base),
  );
  withReport("artifacts/browser/webkit.json", browser(names, "firefox"), (base) =>
    assert.throws(() => verifyBrowser("webkit", base), /identity/u),
  );
  withReport(
    "artifacts/browser/webkit.json",
    { ...browser(names, "webkit"), failureCodes: ["E2E_X"] },
    (base) => assert.throws(() => verifyBrowser("webkit", base), /failures/u),
  );
  assert.throws(() => verifyBrowser("netscape"), /not registered/u);
});
