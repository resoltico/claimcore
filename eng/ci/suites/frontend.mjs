// Verify the structured reports of the frontend unit run and each browser engine against the
// committed inventories: every inventoried test passed exactly once, nothing else ran.
//
//   node eng/ci/suites/frontend.mjs vitest | browser <engine> | all
import { readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseInventory } from "./inventory.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const maximumBytes = 16 * 1024 * 1024;
export const engines = ["chromium", "firefox", "webkit"];

/**
 * @param {unknown} value
 * @param {string[]} keys
 * @param {string} what
 * @returns {Record<string, any>}
 */
function exactObject(value, keys, what) {
  const object = /** @type {Record<string, any>} */ (value);
  if (
    typeof object !== "object" ||
    object === null ||
    Array.isArray(object) ||
    JSON.stringify(Object.keys(object).sort()) !== JSON.stringify([...keys].sort())
  ) {
    throw new Error(`${what} has an unexpected shape.`);
  }
  return object;
}

/**
 * @param {Record<string, any>} totals
 * @param {{ passed: number, zero: string[] }} expected
 */
function checkTotals(totals, { passed, zero }) {
  exactObject(totals, ["passed", ...zero], "Report totals");
  if (totals["passed"] !== passed || zero.some((name) => totals[name] !== 0)) {
    throw new Error("Report totals differ from the inventory or record non-passing outcomes.");
  }
}

/**
 * @param {unknown} tests
 * @param {string[]} inventory
 */
function checkTests(tests, inventory) {
  if (!Array.isArray(tests)) {
    throw new Error("Report tests must be an array.");
  }
  const ids = tests.map((test) => {
    const item = exactObject(test, ["id", "outcome", "durationMs"], "A report test");
    if (
      item["outcome"] !== "passed" ||
      typeof item["id"] !== "string" ||
      item["id"] === "" ||
      item["id"].length > 240
    ) {
      throw new Error("A reported test did not pass.");
    }
    return item["id"];
  });
  if (new Set(ids).size !== ids.length) {
    throw new Error("Report test identities are duplicated.");
  }
  if (JSON.stringify([...ids].sort()) !== JSON.stringify([...inventory].sort())) {
    throw new Error("Report test identities differ from the inventory.");
  }
}

/**
 * @param {string} path
 * @returns {unknown}
 */
function readReport(path) {
  if (statSync(path).size > maximumBytes) {
    throw new Error("Report exceeds its bounded size.");
  }
  return JSON.parse(readFileSync(path, "utf8"));
}

/** @param {string} name */
const inventory = (name) =>
  parseInventory(readFileSync(join(root, "tests/inventory", `${name}.txt`), "utf8"));

/** @param {string} [base] Repository root. */
export function verifyVitest(base = root) {
  const names = inventory("vitest");
  const report = exactObject(
    readReport(join(base, "artifacts/frontend/vitest-summary.json")),
    ["format", "formatVersion", "status", "totals", "tests"],
    "The Vitest report",
  );
  if (
    report["format"] !== "claimcore-vitest-report" ||
    report["formatVersion"] !== 1 ||
    report["status"] !== "passed"
  ) {
    throw new Error("Vitest report identity or status is invalid.");
  }
  checkTotals(report["totals"], { passed: names.length, zero: ["failed", "skipped", "todo"] });
  checkTests(report["tests"], names);
}

/**
 * @param {string} engine
 * @param {string} [base] Repository root.
 */
export function verifyBrowser(engine, base = root) {
  if (!engines.includes(engine)) {
    throw new Error("Browser report engine is not registered.");
  }
  const names = inventory("browser");
  const report = exactObject(
    readReport(join(base, `artifacts/browser/${engine}.json`)),
    [
      "format",
      "formatVersion",
      "scope",
      "status",
      "expected",
      "totals",
      "tests",
      "failureLines",
      "failureCodes",
    ],
    "The browser report",
  );
  if (
    report["format"] !== "claimcore-playwright-report" ||
    report["formatVersion"] !== 1 ||
    report["scope"] !== engine ||
    report["status"] !== "passed" ||
    report["expected"] !== names.length
  ) {
    throw new Error("Browser report identity or status is invalid.");
  }
  checkTotals(report["totals"], {
    passed: names.length,
    zero: ["failed", "skipped", "timedOut", "interrupted"],
  });
  checkTests(report["tests"], names);
  if (report["failureLines"].length !== 0 || report["failureCodes"].length !== 0) {
    throw new Error("Browser report records failures.");
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [kind, engine] = process.argv.slice(2);
  try {
    if (kind === "vitest") {
      verifyVitest();
    } else if (kind === "browser" && engine !== undefined) {
      verifyBrowser(engine);
    } else if (kind === "all") {
      verifyVitest();
      engines.forEach((each) => verifyBrowser(each));
    } else {
      throw new Error("usage: frontend.mjs vitest | browser <engine> | all");
    }
    process.stdout.write("Frontend report inventory passed.\n");
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
