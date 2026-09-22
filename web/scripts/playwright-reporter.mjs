import { readFileSync, writeFileSync } from "node:fs";
import { mkdir, rename, unlink, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { BrowserStepDiagnostic, browserSources } from "./playwright-diagnostics.mjs";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const scope = process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "all";

if (!["all", "chromium", "firefox", "webkit"].includes(scope)) {
  throw new Error("Published browser reports require one recognized engine scope.");
}

const output = resolve(scriptDirectory, `../../artifacts/browser/${scope}.json`);
const diagnostics = resolve(scriptDirectory, `../../artifacts/browser/${scope}.diagnostics.json`);
const layout = resolve(scriptDirectory, `../../artifacts/browser/layout-${scope}.json`);
const progressFile = process.env["CLAIMCORE_WEB_E2E_PROGRESS_FILE"];
const resultKeys = {
  passed: "passed",
  failed: "failed",
  skipped: "skipped",
  timedOut: "timedOut",
  interrupted: "interrupted",
};
const failureStatuses = new Set(["failed", "timedOut"]);

const writeSummary = async (summary) => {
  await mkdir(dirname(output), { recursive: true });
  const temporary = `${output}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(summary, null, 2)}\n`, { mode: 0o600 });
  await rename(temporary, output);
};

const safeStage = () => {
  if (progressFile === undefined) return null;
  try {
    const value = readFileSync(progressFile, "utf8");
    return /^[a-z0-9-]{1,64}$/u.test(value) ? value : null;
  } catch {
    return null;
  }
};

const count = (totals, status) => {
  const key = resultKeys[status];
  if (key !== undefined) totals[key] += 1;
};

const recordFailure = (reporter, result) => {
  if (!failureStatuses.has(result.status)) return;
  reporter.failureLines.push(result.errors[0]?.location?.line ?? null);
  const code = result.errors[0]?.message?.match(/E2E_[A-Z_0-9]+/u)?.[0];
  if (code !== undefined) reporter.failureCodes.push(code);
};

export default class SanitizedPlaywrightReporter {
  expected = 0;
  sourceLocations = browserSources(resolve(scriptDirectory, "../.."));
  stepDiagnostics = new WeakMap();
  totals = { passed: 0, failed: 0, skipped: 0, timedOut: 0, interrupted: 0 };
  tests = [];
  failureLines = [];
  failureCodes = [];
  diagnosticTests = [];

  onBegin(_config, suite) {
    this.expected = suite.allTests().length;
  }

  onTestBegin(_test, result) {
    this.stepDiagnostics.set(result, new BrowserStepDiagnostic(this.sourceLocations));
    if (progressFile !== undefined) writeFileSync(progressFile, "test-start");
  }

  onStepBegin(_test, result, step) {
    this.stepDiagnostics.get(result)?.begin(step);
  }

  onStepEnd(_test, result, step) {
    this.stepDiagnostics.get(result)?.end(step);
  }

  onTestEnd(test, result) {
    count(this.totals, result.status);
    this.tests.push({ id: test.title, outcome: result.status, durationMs: result.duration });
    recordFailure(this, result);
    this.diagnosticTests.push({
      id: test.title,
      status: result.status,
      stage: safeStage(),
      line: result.errors[0]?.location?.line ?? null,
      step: this.stepDiagnostics.get(result)?.snapshot() ?? null,
    });
  }

  async onEnd(result) {
    await writeSummary({
      format: "claimcore-playwright-report",
      formatVersion: 1,
      scope,
      status: result.status,
      expected: this.expected,
      totals: this.totals,
      tests: this.tests.sort((left, right) => left.id.localeCompare(right.id)),
      failureLines: this.failureLines,
      failureCodes: this.failureCodes,
    });
    if (result.status === "passed") {
      await Promise.all([diagnostics, layout].map((path) => unlink(path).catch(() => undefined)));
    } else await writeFile(diagnostics, `${JSON.stringify(this.diagnosticTests, null, 2)}\n`);
  }
}
