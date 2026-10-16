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
    if (reason === "failed" || errors.length > 0) {
      return;
    }
    const tests = modules.flatMap((module) => [...module.children.allTests()]);
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
