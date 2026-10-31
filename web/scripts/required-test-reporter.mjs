import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

/** Reject incomplete runner outcomes, independent of import syntax or test API chaining. */
export default class RequiredTestReporter {
  /** @param {import("vitest/node").Vitest} context */
  onInit(context) {
    if (context.config.allowOnly) {
      throw new Error("Required test completion refused: allowOnly must be false.");
    }
  }
  /** @param {ReadonlyArray<import("vitest/node").TestModule>} modules
   * @param {ReadonlyArray<unknown>} errors
   * @param {"passed"|"failed"|"interrupted"} reason */
  onTestRunEnd(modules, errors, reason) {
    const tests = modules.flatMap((module) => [...module.children.allTests()]);
    // No test titles, error messages, request values or exception data enter CI evidence.
    const states = { passed: 0, failed: 0, skipped: 0, pending: 0, unknown: 0 };
    for (const test of tests) {
      const { state } = test.result();
      if (state === "passed" || state === "failed" || state === "skipped" || state === "pending") {
        states[state] += 1;
      } else {
        states.unknown += 1;
      }
    }
    const directory = resolve(import.meta.dirname, "../../artifacts/verification/frontend");
    mkdirSync(directory, { recursive: true });
    writeFileSync(
      resolve(directory, "completion.json"),
      `${JSON.stringify({
        reason,
        modules: modules.length,
        tests: tests.length,
        errors: errors.length,
        states,
      })}\n`,
    );
    if (reason === "failed" || errors.length > 0) {
      return;
    }
    const incomplete = tests.some((test) => {
      const result = test.result();
      return (
        test.options.mode === "only" ||
        result.state === "skipped" ||
        result.state === "pending" ||
        (test.diagnostic()?.retryCount ?? 0) > 0
      );
    });
    const excludedSuite = modules.some((module) =>
      [...module.children.allSuites()].some(
        (suite) =>
          suite.options.mode === "skip" ||
          suite.options.mode === "todo" ||
          suite.options.mode === "only",
      ),
    );
    if (reason === "interrupted" || tests.length === 0 || incomplete || excludedSuite) {
      throw new Error(
        "Required test completion refused: empty, interrupted, excluded or retried tests.",
      );
    }
  }
}
