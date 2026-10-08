import { createHash } from "node:crypto";
import { mkdir, rename, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const output = resolve(scriptDirectory, "../../artifacts/frontend/vitest-summary.json");

/** @param {unknown} summary */
const writeSummary = async (summary) => {
  await mkdir(dirname(output), { recursive: true });
  const temporary = `${output}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(summary, null, 2)}\n`, { mode: 0o600 });
  await rename(temporary, output);
};

export default class SanitizedVitestReporter {
  totals = { passed: 0, failed: 0, skipped: 0, todo: 0 };
  /** @type {import("./tooling-types.mjs").TestSummary[]} */
  tests = [];
  /** @type {{ testIdSha256: string, line: number | null }[]} */
  failures = [];

  /** @param {unknown[]} errors */
  failureSummary(errors) {
    return {
      category: "vitest-failure",
      failedTests: this.totals.failed,
      runErrors: errors.length,
      tests: this.failures,
    };
  }

  /** @param {import("vitest/node").TestCase} testCase */
  recordFailure(testCase) {
    if (this.failures.length >= 20) {
      return;
    }
    const line = testCase.location?.line;
    this.failures.push({
      testIdSha256: createHash("sha256").update(testCase.fullName).digest("hex"),
      line:
        typeof line === "number" && Number.isInteger(line) && line > 0 && line <= 2_147_483_647
          ? line
          : null,
    });
  }

  /** @param {import("vitest/node").TestCase} testCase */
  onTestCaseResult(testCase) {
    const result = testCase.result();
    const { state } = result;
    const duration = testCase.diagnostic()?.duration ?? 0;
    if (!Number.isFinite(duration) || duration < 0 || duration > 2_147_483_647) {
      throw new Error("Vitest reported an invalid test duration.");
    }
    this.tests.push({
      id: testCase.fullName,
      outcome: state,
      durationMs: Math.ceil(duration),
    });
    if (state === "passed") {
      this.totals.passed += 1;
    } else if (state === "failed") {
      this.totals.failed += 1;
      this.recordFailure(testCase);
    } else if (state === "skipped" && testCase.options.mode === "todo") {
      this.totals.todo += 1;
    } else if (state === "skipped") {
      this.totals.skipped += 1;
    }
  }

  /** @param {unknown[]} _modules @param {unknown[]} errors @param {string} status */
  async onTestRunEnd(_modules, errors, status) {
    if (status !== "passed" || this.totals.failed > 0 || errors.length > 0) {
      // Each stage keeps its own log even if a later consumer overwrites the summary.
      // Hash identities permit inventory lookup without rendering test or error payloads.
      process.stderr.write(`${JSON.stringify(this.failureSummary(errors))}\n`);
    }
    await writeSummary({
      format: "claimcore-vitest-report",
      formatVersion: 1,
      status,
      totals: this.totals,
      tests: this.tests.sort((left, right) => left.id.localeCompare(right.id)),
    });
  }
}
