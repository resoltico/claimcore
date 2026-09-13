import { mkdir, rename, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const output = resolve(scriptDirectory, "../../artifacts/frontend/vitest-summary.json");

const writeSummary = async (summary) => {
  await mkdir(dirname(output), { recursive: true });
  const temporary = `${output}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(summary, null, 2)}\n`, { mode: 0o600 });
  await rename(temporary, output);
};

export default class SanitizedVitestReporter {
  totals = { passed: 0, failed: 0, skipped: 0, todo: 0 };
  tests = [];

  onTestCaseResult(testCase) {
    const result = testCase.result();
    const state = result.state;
    this.tests.push({
      id: testCase.fullName,
      outcome: state,
      durationMs: testCase.diagnostic()?.duration ?? 0,
    });
    if (state === "passed") this.totals.passed += 1;
    else if (state === "failed") this.totals.failed += 1;
    else if (state === "skipped" && testCase.options.mode === "todo") this.totals.todo += 1;
    else if (state === "skipped") this.totals.skipped += 1;
  }

  async onTestRunEnd(_modules, _errors, status) {
    await writeSummary({
      format: "claimcore-vitest-report",
      formatVersion: 1,
      status,
      totals: this.totals,
      tests: this.tests.sort((left, right) => left.id.localeCompare(right.id)),
    });
  }
}
