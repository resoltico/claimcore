import test from "node:test";
import assert from "node:assert/strict";
import {
  BrowserStepDiagnostic,
  startupDiagnostic,
} from "../../web/scripts/playwright-diagnostics.mjs";

const sources = new Map([
  ["/repo/web/e2e/check.spec.ts", { file: "web/e2e/check.spec.ts", lines: 100 }],
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
  assert.equal(first.snapshot()?.line, 12);
  const second = new BrowserStepDiagnostic(sources);
  assert.equal(second.snapshot(), null);
});

const startup = {
  truncated: false,
  authenticated: true,
  pageErrorSeen: false,
  readiness: { readyState: "interactive", view: "loading", alert: false },
  requests: [{ kind: "session", status: 200, phase: "headers", elapsedMs: 5000 }],
};
/** @param {Record<string, unknown>} value */
const encoded = (value) => Buffer.from(JSON.stringify(value));

test("startup evidence admits only bounded status and readiness observations", () => {
  assert.deepEqual(startupDiagnostic(encoded(startup)), startup);
  const unavailable = { ...startup, authenticated: null, readiness: null, truncated: true };
  assert.deepEqual(startupDiagnostic(encoded(unavailable)), unavailable);
  assert.equal(startupDiagnostic(undefined), null);
});

test("startup evidence refuses arbitrary attachment fields and unbounded observations", () => {
  const request = startup.requests[0];
  for (const value of [
    { ...startup, url: "PRIVATE-URL-SECRET" },
    { ...startup, authenticated: "PRIVATE-TOKEN-SECRET" },
    { ...startup, pageErrorSeen: "PRIVATE-ERROR-SECRET" },
    { ...startup, readiness: { ...startup.readiness, view: "PRIVATE-DOM-SECRET" } },
    { ...startup, readiness: { ...startup.readiness, text: "PRIVATE-DOM-SECRET" } },
    { ...startup, requests: [{ ...request, body: "PRIVATE-PAYLOAD-SECRET" }] },
    { ...startup, requests: [{ ...request, status: 700 }] },
    { ...startup, requests: [{ ...request, elapsedMs: 60001 }] },
    { ...startup, requests: [{ ...request, phase: "PRIVATE-ERROR-SECRET" }] },
    { ...startup, requests: Array(41).fill(request) },
  ]) {
    assert.equal(startupDiagnostic(encoded(value)), null);
  }
  assert.equal(startupDiagnostic(Buffer.from("not JSON")), null);
  assert.equal(startupDiagnostic(Buffer.alloc(16385)), null);
});
