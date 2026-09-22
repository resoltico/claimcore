import test from "node:test";
import assert from "node:assert/strict";
import { stageDiagnostic } from "./stage-diagnostics.mjs";

const base = {
  producer: "quality",
  stage: "fsharplint",
  exitCode: 1,
  manifestExit: 0,
  root: "/repo",
  tracked: new Set(["src/Test.fs"]),
  log: "",
};
test("safe failure reports retain only allowlisted source locations", () => {
  const report = stageDiagnostic({
    ...base,
    log: "/repo/src/Test.fs(12,3): error FS0001: PRIVATE-CANARY\n/private/credential(1,1): error FS0002: SECRET\nConnection=PRIVATE\n",
  });
  assert.deepEqual(report.findings, [
    { file: "src/Test.fs", line: 12, column: 3, rule: "FS0001" },
  ]);
  assert(!JSON.stringify(report).includes("PRIVATE"));
  assert(!JSON.stringify(report).includes("SECRET"));
});
test("failure of procedure or evidence is never disguised by reporting", () => {
  assert.equal(stageDiagnostic(base).outcome, "procedure-failed");
  assert.equal(
    stageDiagnostic({ ...base, exitCode: 0, manifestExit: 2 }).outcome,
    "evidence-failed",
  );
  assert.equal(stageDiagnostic({ ...base, exitCode: 0 }).outcome, "passed");
});
test("reporting refuses unknown stage identities and invalid statuses", () => {
  assert.throws(() => stageDiagnostic({ ...base, stage: "SECRET" }));
  assert.throws(() => stageDiagnostic({ ...base, exitCode: NaN }));
  assert.throws(() => stageDiagnostic({ ...base, manifestExit: -1 }));
});
test("reporting is bounded and never publishes raw diagnostic text", () => {
  const report = stageDiagnostic({
    ...base,
    log: "x".repeat(3 * 1024 * 1024) + "\n[warn] src/Test.fs\nPRIVATE-CANARY",
  });
  assert.deepEqual(report.findings, [{ file: "src/Test.fs" }]);
  assert.equal(report.rawLogPublished, false);
  assert(JSON.stringify(report).length < 2048);
});
