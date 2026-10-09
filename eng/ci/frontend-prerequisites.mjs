// Reuse only completed frontend prerequisites from this exact admitted local run.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { lstatSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { artifactDirectory } from "./artifact-path.mjs";
import { runContext } from "./run-context.mjs";
import { frontendPrerequisiteReports } from "./run-frontend-reports.mjs";
import { assertNoLinkAbove } from "./scan/files.mjs";

export const prerequisiteStages = ["frontend-unit", "frontend-report", "frontend-mutation"];
const reports = [
  "artifacts/frontend/vitest-summary.json",
  "artifacts/frontend/coverage/coverage-summary.json",
  "web/artifacts/stryker/domain-mutation.json",
];
/** @param {string} root */
const receiptPath = (root) =>
  join(artifactDirectory(root, "artifacts/frontend"), "prerequisites.json");
/** @param {string} path */
function regular(path) {
  assertNoLinkAbove(path);
  assert.ok(lstatSync(path).isFile(), "Frontend prerequisite evidence must be a regular file.");
}
/** @param {import("./run-context.mjs").RunContext} context */
function current(context) {
  assert.deepEqual(runContext(context.source, join(context.scratch, "context.json")), context);
}
/** @param {string} root */
function reportHashes(root) {
  return Object.fromEntries(
    reports.map((report) => {
      const path = join(root, report);
      regular(path);
      assert.ok(
        lstatSync(path).size <= 16 * 1024 * 1024,
        "Frontend prerequisite report exceeds its bound.",
      );
      const bytes = readFileSync(path);
      assert.ok(
        bytes.length <= 16 * 1024 * 1024,
        "Frontend prerequisite report exceeds its bound.",
      );
      regular(path);
      return [report, createHash("sha256").update(bytes).digest("hex")];
    }),
  );
}
/** @param {import("./run-context.mjs").RunContext} context */
function reportIdentity(context) {
  const before = reportHashes(context.source);
  assert.deepEqual(
    frontendPrerequisiteReports(context.source).sort(),
    reports.map((report) => join(context.source, report)).sort(),
  );
  assert.deepEqual(
    reportHashes(context.source),
    before,
    "Frontend reports changed during owner admission.",
  );
  return before;
}
/** @param {import("./run-context.mjs").RunContext} context */
function receipt(context) {
  return {
    format: 1,
    runId: context.id,
    sourceSha256: context.sourceSha256,
    producingInputsSha256: context.producingInputsSha256,
    reports: reportIdentity(context),
  };
}
/** Validate selection first; invalidate before any selected producer can change its reports.
 * @param {string} root @param {string[]} selected
 * @returns {import("./run-context.mjs").RunContext | null}
 */
export function beginFrontendPrerequisites(root, selected) {
  const context = runContext(root);
  if (context !== null && selected.some((id) => prerequisiteStages.includes(id))) {
    const path = receiptPath(root);
    if (lstatSync(path, { throwIfNoEntry: false })) {
      regular(path);
      rmSync(path);
    }
    current(context);
  }
  return context;
}
/** Actual Vite success is required: a passing test report alone cannot prove coverage floors.
 * @param {import("./run-context.mjs").RunContext | null} context
 * @param {import("./stage-plan.mjs").Outcome[]} results
 */
export function finishFrontendPrerequisites(context, results) {
  if (
    context === null ||
    !prerequisiteStages.every((id) =>
      results.some((result) => result.stage.id === id && result.value.status === "passed"),
    )
  ) {
    return;
  }
  current(context);
  const value = receipt(context);
  current(context);
  const path = receiptPath(context.source);
  mkdirSync(artifactDirectory(context.source, "artifacts/frontend"), {
    recursive: true,
    mode: 0o700,
  });
  writeFileSync(path, `${JSON.stringify(value)}\n`, { flag: "wx", mode: 0o600 });
  current(context);
}
/** @param {import("./run-context.mjs").RunContext} context */
export function verifyFrontendPrerequisites(context) {
  current(context);
  const path = receiptPath(context.source);
  regular(path);
  assert.ok(lstatSync(path).size <= 4096, "Frontend prerequisite receipt exceeds its bound.");
  const bytes = readFileSync(path);
  assert.ok(bytes.length <= 4096, "Frontend prerequisite receipt exceeds its bound.");
  assert.deepEqual(JSON.parse(bytes.toString("utf8")), receipt(context));
  current(context);
}
/** @param {string} root @returns {number | null} */
function executePrerequisites(root) {
  return spawnSync(
    process.execPath,
    ["eng/ci/run-stages.mjs", "frontend", "--only", prerequisiteStages.join(",")],
    { cwd: root, stdio: "inherit", env: process.env },
  ).status;
}
/** Absence runs the producers; a present but invalid receipt never silently regenerates.
 * @param {string} root @param {(root:string)=>number|null} [execute]
 */
export function ensureFrontendPrerequisites(root, execute = executePrerequisites) {
  const context = runContext(root);
  assert.ok(context, "Frontend prerequisite reuse requires an admitted local run.");
  const path = receiptPath(root);
  if (!lstatSync(path, { throwIfNoEntry: false })) {
    assert.equal(execute(root), 0, "Frontend prerequisite commands failed.");
  }
  verifyFrontendPrerequisites(context);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  assert.equal(process.argv.length, 2);
  ensureFrontendPrerequisites(fileURLToPath(new URL("../..", import.meta.url)));
}
