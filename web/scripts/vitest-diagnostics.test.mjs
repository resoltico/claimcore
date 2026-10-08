import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import SanitizedVitestReporter from "./vitest-reporter.mjs";

/** @param {SanitizedVitestReporter} reporter @param {string} id @param {unknown} line */
function fail(reporter, id, line) {
  reporter.onTestCaseResult(
    /** @type {Parameters<SanitizedVitestReporter["onTestCaseResult"]>[0]} */ (
      /** @type {unknown} */ ({
        fullName: id,
        location: { line },
        result: () => ({ state: "failed", errors: [new Error("PRIVATE-ERROR-PAYLOAD")] }),
        diagnostic: () => ({ duration: 1 }),
      })
    ),
  );
}

test("Vitest failure diagnostics preserve hashed identity and definition line without payloads", () => {
  const reporter = new SanitizedVitestReporter();
  const id = "PRIVATE-TEST-PAYLOAD";
  fail(reporter, id, 27);
  const diagnostic = reporter.failureSummary([new Error("PRIVATE-RUN-PAYLOAD")]);
  assert.deepEqual(diagnostic, {
    category: "vitest-failure",
    failedTests: 1,
    runErrors: 1,
    tests: [{ testIdSha256: createHash("sha256").update(id).digest("hex"), line: 27 }],
  });
  assert(!JSON.stringify(diagnostic).includes("PRIVATE"));
});

test("Vitest failure diagnostics bound identities and refuse malformed source lines", () => {
  const reporter = new SanitizedVitestReporter();
  for (const line of ["PRIVATE-LINE", undefined, -1, 0, NaN, Infinity, 1.5, 2_147_483_648]) {
    fail(reporter, "PRIVATE", line);
  }
  assert(reporter.failures.every((failure) => failure.line === null));
  for (let index = 0; index < 30; index += 1) {
    fail(reporter, `PRIVATE-${index}`, 5);
  }
  const diagnostic = reporter.failureSummary([]);
  assert.equal(diagnostic.failedTests, 38);
  assert.equal(diagnostic.tests.length, 20);
  assert(!JSON.stringify(diagnostic).includes("PRIVATE"));
});
