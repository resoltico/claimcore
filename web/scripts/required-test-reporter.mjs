/** Reject incomplete runner outcomes, independent of import syntax or test API chaining. */
export default class RequiredTestReporter {
  /** @type {RegExp | undefined} */
  selection;

  /** @param {import("vitest/node").TestCase} test */
  selected(test) {
    if (this.selection === undefined) {
      return true;
    }
    const names = [test.name];
    let { parent } = test;
    while (parent.type === "suite") {
      names.unshift(parent.name);
      ({ parent } = parent);
    }
    this.selection.lastIndex = 0;
    return this.selection.test(names.join(" "));
  }

  /** @param {import("vitest/node").Vitest} context */
  onInit(context) {
    this.selection =
      process.env["CI"] || process.env["GITHUB_ACTIONS"]
        ? undefined
        : context.config.testNamePattern;
    if (context.config.allowOnly) {
      throw new Error("Required test completion refused: allowOnly must be false.");
    }
  }
  /** @param {ReadonlyArray<import("vitest/node").TestModule>} modules
   * @param {ReadonlyArray<unknown>} errors
   * @param {"passed"|"failed"|"interrupted"} reason */
  onTestRunEnd(modules, errors, reason) {
    const tests = modules.flatMap((module) => [...module.children.allTests()]);
    if (reason === "failed" || errors.length > 0) {
      return;
    }
    const selected = tests.filter((test) => this.selected(test));
    const incomplete = selected.some((test) => {
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
          suite.options.mode === "only" ||
          ((suite.options.mode === "skip" || suite.options.mode === "todo") &&
            [...suite.children.allTests()].some((test) => this.selected(test))),
      ),
    );
    if (reason === "interrupted" || selected.length === 0 || incomplete || excludedSuite) {
      throw new Error(
        "Required test completion refused: empty, interrupted, excluded or retried tests.",
      );
    }
  }
}
