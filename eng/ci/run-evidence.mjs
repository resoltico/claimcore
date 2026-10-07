import { requirePassedEvidence } from "./run-expectations.mjs";
import { admittedReports } from "./run-reports.mjs";
// Retain regular reports and input manifests once; publications and dependencies are scratch.
import assert from "node:assert/strict";
import { constants, copyFileSync, existsSync, mkdirSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { removeSettledScratch, untouchedSnapshot } from "./run-scratch.mjs";
import { sourceFingerprint } from "./source-snapshot.mjs";
import { artifactDirectory } from "./artifact-path.mjs";
import { contextEnvironment, runContext } from "./run-context.mjs";
import { assertNoLinkAbove, fingerprint, regularFiles } from "./scan/files.mjs";
import { scanArtifacts } from "./scan/artifacts.mjs";
import { installTool } from "./tools.mjs";
import { observeHistory } from "./scan/history.mjs";

/** @param {import("./run-context.mjs").RunContext} context @param {boolean} passed */
async function retainResults(context, passed) {
  const staging = artifactDirectory(context.source, "artifacts/retained-evidence");
  assert.ok(!existsSync(staging) && !existsSync(context.results));
  mkdirSync(staging, { mode: 0o700 });
  /** @type {string[]} */
  let files = [];
  let qualified = true;
  try {
    files = admittedReports(context);
  } catch {
    if (passed) {
      throw new Error("A required report failed independent evidence admission.");
    }
    qualified = false;
  }
  for (const file of files) {
    const destination = join(staging, relative(context.source, file));
    mkdirSync(dirname(destination), { recursive: true, mode: 0o700 });
    renameSync(file, destination);
  }
  writeFileSync(
    join(staging, "admission.json"),
    `${JSON.stringify({ format: 1, run: context.id, qualified, files: files.map((file) => relative(context.source, file)) })}\n`,
    { mode: 0o600 },
  );
  const before = fingerprint(staging);
  assert.equal(
    await scanArtifacts([staging, join(dirname(context.results), "inputs")], {
      gitleaks: () => installTool(context.origin, "gitleaks"),
    }),
    0,
  );
  assert.equal(fingerprint(staging), before);
  artifactDirectory(context.origin, relative(context.origin, context.results));
  runContext(context.source, contextEnvironment(context)["CLAIMCORE_RUN_CONTEXT"]);
  assert.deepEqual(observeHistory(context.origin), context.history);
  assert.equal(sourceFingerprint(context.origin), context.sourceSha256);
  transferEvidence(staging, context.results, before);
  runContext(context.source, contextEnvironment(context)["CLAIMCORE_RUN_CONTEXT"]);
  assert.deepEqual(observeHistory(context.origin), context.history);
  assert.equal(sourceFingerprint(context.origin), context.sourceSha256);
}
/** Copy once into fresh inodes: a producer's held descriptor must not mutate retained evidence.
 * @param {string} staging @param {string} destinationRoot @param {string} qualifiedFingerprint */
export function transferEvidence(staging, destinationRoot, qualifiedFingerprint) {
  assertNoLinkAbove(staging);
  assertNoLinkAbove(dirname(destinationRoot));
  assert.equal(fingerprint(staging), qualifiedFingerprint);
  mkdirSync(destinationRoot, { mode: 0o700 });
  for (const file of regularFiles(staging)) {
    const destination = join(destinationRoot, relative(staging, file));
    mkdirSync(dirname(destination), { recursive: true, mode: 0o700 });
    assertNoLinkAbove(dirname(destination));
    copyFileSync(file, destination, constants.COPYFILE_EXCL);
  }
  assert.equal(fingerprint(staging), qualifiedFingerprint);
  assertNoLinkAbove(destinationRoot);
  assert.equal(fingerprint(destinationRoot), qualifiedFingerprint);
}
/** @param {import("./run-context.mjs").RunContext} context @param {{passed:boolean,groups:number[],stages?:Record<string,string>}} outcome */
export async function finishRun(context, { passed, groups, stages = {} }) {
  runContext(context.source, contextEnvironment(context)["CLAIMCORE_RUN_CONTEXT"]);
  assert.deepEqual(await observeHistory(context.origin), context.history);
  assert.equal(sourceFingerprint(context.origin), context.sourceSha256);
  if (passed) {
    requirePassedEvidence(context, stages);
  }
  await retainResults(context, passed);
  // Process-group absence cannot prove detached descendant settlement. A run that executed
  // jobs preserves unknown snapshot ownership; only an untouched no-job snapshot is disposable.
  const settled = passed && groups.length === 0 && untouchedSnapshot(context);
  if (settled) {
    removeSettledScratch(context);
  }
  writeFileSync(
    join(dirname(context.results), "outcome.json"),
    `${JSON.stringify({ passed, stages, scratchRemoved: settled })}\n`,
    { flag: "wx", mode: 0o600 },
  );
  console.log(`Retained admitted evidence: ${context.results}.`);
  if (!settled) {
    console.log(`Failed or unsettled scratch retained: ${context.scratch}.`);
  }
}
