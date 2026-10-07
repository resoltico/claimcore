// Explicit caller-selected job ownership; this record does not authenticate a CI provider.
import assert from "node:assert/strict";
import {
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { artifactDirectory } from "./artifact-path.mjs";
import { producingInputDigest } from "./publish/inputs.mjs";
import { assertNoLinkAbove } from "./scan/files.mjs";
import { observeHistory } from "./scan/history.mjs";
import { sourceFingerprint } from "./source-snapshot.mjs";

/** @param {string} root */
function observation(root) {
  root = resolve(root);
  assertNoLinkAbove(root);
  assert.equal(realpathSync(root), root);
  const history = observeHistory(root);
  const value = {
    format: 1,
    kind: "job-checkout",
    root,
    history,
    sourceSha256: sourceFingerprint(root),
    producingInputsSha256: producingInputDigest(root),
  };
  assert.deepEqual(observeHistory(root), history);
  return value;
}

/** @param {string} root @returns {string} */
export function createJobContext(root) {
  const value = observation(root);
  const path = join(artifactDirectory(value.root, "artifacts/job-ownership"), "context.json");
  assert.ok(!existsSync(path), "A job context must start absent.");
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx", mode: 0o600 });
  assert.deepEqual(observation(value.root), value);
  return path;
}

/** @param {string} root @param {string} path */
function validate(root, path) {
  root = resolve(root);
  assert.equal(path, join(artifactDirectory(root, "artifacts/job-ownership"), "context.json"));
  assertNoLinkAbove(path);
  const stat = lstatSync(path);
  assert.ok(stat.isFile() && stat.size < 16 * 1024);
  if (process.platform !== "win32") {
    assert.equal(stat.mode & 0o077, 0, "A job context must be owner-private.");
  }
  const value = JSON.parse(readFileSync(path, "utf8"));
  assert.deepEqual(value, observation(root), "Job source or Git graph changed after admission.");
  return value;
}

/** @param {string} root @param {string | undefined} [path] */
export function jobContext(root, path = process.env["CLAIMCORE_JOB_CONTEXT"]) {
  if (path === undefined) {
    return null;
  }
  try {
    return validate(root, path);
  } catch {
    throw new Error("Job-checkout context is absent, inconsistent or changed.");
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const root = resolve(import.meta.dirname, "../..");
    const [mode] = process.argv.slice(2);
    assert.equal(process.argv.length, 3);
    if (mode === "create") {
      process.stdout.write(`CLAIMCORE_JOB_CONTEXT=${createJobContext(root)}\n`);
    } else {
      assert.equal(mode, "check");
      assert.ok(jobContext(root), "An explicit job-checkout context is required.");
    }
  } catch {
    process.stderr.write("Job-checkout context is absent, inconsistent or changed.\n");
    process.exitCode = 1;
  }
}
