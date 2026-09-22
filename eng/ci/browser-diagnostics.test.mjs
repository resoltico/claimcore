import test from "node:test";
import assert from "node:assert/strict";
import { BrowserStepDiagnostic } from "../../web/scripts/playwright-diagnostics.mjs";

const sources = new Map([
  [
    "/repo/web/e2e/check.spec.ts",
    { file: "web/e2e/check.spec.ts", lines: 100 },
  ],
]);
const step = {
  category: "pw:api",
  title: "PRIVATE-SELECTOR-SECRET",
  location: { file: "/repo/web/e2e/check.spec.ts", line: 12, column: 3 },
};
test("browser diagnostics preserve safe first-failure location without raw step content", () => {
  const diagnostic = new BrowserStepDiagnostic(sources);
  diagnostic.begin(step);
  diagnostic.end({ ...step, error: { message: "PRIVATE-PROVIDER-SECRET" } });
  diagnostic.begin({ ...step, location: { ...step.location, line: 60 } });
  assert.deepEqual(diagnostic.snapshot(), {
    category: "pw:api",
    file: "web/e2e/check.spec.ts",
    line: 12,
    column: 3,
  });
  assert(!JSON.stringify(diagnostic.snapshot()).includes("PRIVATE"));
});
test("browser diagnostics reject unknown source paths categories and impossible locations", () => {
  for (const changed of [
    { ...step, category: "PRIVATE" },
    { ...step, location: { ...step.location, file: "/private/key" } },
    { ...step, location: { ...step.location, line: 101 } },
    { ...step, location: { ...step.location, line: -1 } },
    { ...step, location: { ...step.location, column: NaN } },
  ]) {
    const diagnostic = new BrowserStepDiagnostic(sources);
    diagnostic.begin(changed);
    diagnostic.end({ ...changed, error: new Error("PRIVATE") });
    assert.equal(diagnostic.snapshot(), null);
  }
});
test("browser diagnostics keep unfinished safe location without inheriting another test", () => {
  const first = new BrowserStepDiagnostic(sources);
  first.begin(step);
  first.end(step);
  assert.equal(first.snapshot().line, 12);
  const second = new BrowserStepDiagnostic(sources);
  assert.equal(second.snapshot(), null);
});
