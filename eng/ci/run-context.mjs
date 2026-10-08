import { protectScratch, requirePrivateContext } from "./scratch-privacy.mjs";
import { jobContext } from "./job-context.mjs";
// A local run binds one Gitless source snapshot to one retained evidence destination.
import { spawnSync } from "node:child_process";
import { gitEnvironment } from "./scan/process.mjs";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { artifactDirectory } from "./artifact-path.mjs";
import { copySource, sourceFingerprint } from "./source-snapshot.mjs";
import { producingInputDigest } from "./publish/inputs.mjs";
import { assertNoLinkAbove } from "./scan/files.mjs";
import { observeHistory } from "./scan/history.mjs";

/** @typedef {Awaited<ReturnType<typeof observeHistory>>} History */
/** @typedef {{format:1,id:string,origin:string,scratch:string,source:string,results:string,sourceSha256:string,producingInputsSha256:string,history:History,requested:string[]}} RunContext */
/** @param {string} path */
function physical(path) {
  assertNoLinkAbove(path);
  assert.equal(realpathSync(path), resolve(path));
  return path;
}
/** @param {string} scratch */
function outsideGit(scratch) {
  physical(scratch);
  const result = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    cwd: scratch,
    env: gitEnvironment(),
    encoding: "utf8",
  });
  assert.equal(result.status, 128, "Run scratch must be outside every Git repository ancestor.");
}
/** @param {string} origin @returns {Promise<RunContext>} */
export async function createRun(origin) {
  origin = physical(realpathSync(origin));
  const history = await observeHistory(origin);
  const id = randomUUID();
  const scratch = physical(mkdtempSync(join(realpathSync(tmpdir()), "claimcore-run-")));
  outsideGit(scratch);
  protectScratch(scratch);
  const source = join(scratch, "source");
  mkdirSync(source, { mode: 0o700 });
  const sourceSha256 = copySource(origin, source);
  assert.deepEqual(await observeHistory(origin), history);
  const retained = artifactDirectory(origin, `artifacts/runs/${id}`);
  assert.ok(!existsSync(retained));
  mkdirSync(retained, { recursive: true, mode: 0o700 });
  const inputs = join(retained, "inputs");
  mkdirSync(inputs, { mode: 0o700 });
  assert.equal(copySource(source, inputs), sourceSha256);
  const context = {
    format: /** @type {const} */ (1),
    id,
    origin,
    scratch,
    source,
    results: join(retained, "results"),
    sourceSha256,
    producingInputsSha256: producingInputDigest(source),
    history,
    requested: process.argv.slice(1),
  };
  const bytes = `${JSON.stringify(context, null, 2)}\n`;
  writeFileSync(join(retained, "run-input.json"), bytes, { flag: "wx", mode: 0o600 });
  writeFileSync(join(scratch, "context.json"), bytes, { flag: "wx", mode: 0o600 });
  return context;
}
/** @param {string} root @param {string} path @returns {RunContext} */
function validateRunContext(root, path) {
  physical(path);
  requirePrivateContext(path);
  const value = /** @type {RunContext} */ (JSON.parse(readFileSync(path, "utf8")));
  assert.deepEqual(
    Object.keys(value).sort(),
    [
      "format",
      "id",
      "origin",
      "scratch",
      "source",
      "results",
      "sourceSha256",
      "producingInputsSha256",
      "history",
      "requested",
    ].sort(),
  );
  assert.equal(value.format, 1);
  assert.ok(
    Array.isArray(value.requested) && value.requested.every((part) => typeof part === "string"),
  );
  assert.match(value.id, /^[0-9a-f-]{36}$/u);
  assert.equal(physical(resolve(root)), value.source);
  assert.equal(dirname(value.source), physical(value.scratch));
  outsideGit(value.scratch);
  assert.equal(path, join(value.scratch, "context.json"));
  assert.equal(value.source, join(value.scratch, "source"));
  assert.ok(!existsSync(join(value.source, ".git")));
  assert.equal(
    value.results,
    artifactDirectory(physical(value.origin), `artifacts/runs/${value.id}/results`),
  );
  assert.equal(
    readFileSync(join(dirname(value.results), "run-input.json"), "utf8"),
    readFileSync(path, "utf8"),
  );
  assert.deepEqual(observeHistory(value.origin), value.history);
  assert.equal(sourceFingerprint(join(dirname(value.results), "inputs")), value.sourceSha256);
  assert.equal(sourceFingerprint(value.source), value.sourceSha256);
  assert.equal(producingInputDigest(value.source), value.producingInputsSha256);
  return value;
}
/** @param {string} root @param {string | undefined} [path] @returns {RunContext | null} */
export function runContext(root, path = process.env["CLAIMCORE_RUN_CONTEXT"]) {
  if (path === undefined) {
    return null;
  }
  try {
    return validateRunContext(root, path);
  } catch {
    throw new Error("Local run context is absent, inconsistent or changed.");
  }
}
/** @param {RunContext} context */
export function contextEnvironment(context) {
  const environment = { ...process.env };
  delete environment["CLAIMCORE_JOB_CONTEXT"];
  return { ...environment, CLAIMCORE_RUN_CONTEXT: join(context.scratch, "context.json") };
}
/** @param {string} root */
export async function historyRoot(root) {
  const context = runContext(root);
  if (context === null) {
    jobContext(root);
    return root;
  }
  assert.deepEqual(await observeHistory(context.origin), context.history);
  return context.origin;
}
