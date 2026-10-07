import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { requirePassedEvidence } from "./run-expectations.mjs";
import { admittedReports } from "./run-reports.mjs";
import { removeSettledScratch } from "./run-scratch.mjs";
import { finishRun } from "./run-evidence.mjs";
import { runToLog } from "./process-support.mjs";
import { setTimeout as delay } from "node:timers/promises";
import { createRun, runContext } from "./run-context.mjs";
import { copySource } from "./source-snapshot.mjs";
import { gitEnvironment } from "./scan/process.mjs";

const root = resolve(import.meta.dirname, "../..");
/** @param {(origin:string,runs:import("./run-context.mjs").RunContext[])=>Promise<void>} body */
async function fixture(body) {
  const origin = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-run-test-"));
  /** @type {import("./run-context.mjs").RunContext[]} */
  const runs = [];
  try {
    copySource(root, origin);
    for (const args of [
      ["init", "-b", "main"],
      ["config", "commit.gpgsign", "false"],
      ["config", "core.hooksPath", join(origin, ".git/owned-empty-hooks")],
      ["add", "-A"],
      [
        "-c",
        "user.name=Synthetic",
        "-c",
        "user.email=synthetic@example.invalid",
        "commit",
        "-m",
        "synthetic",
      ],
    ]) {
      execFileSync("git", args, { cwd: origin, env: gitEnvironment(), stdio: "pipe" });
    }
    await body(origin, runs);
  } finally {
    for (const run of runs) {
      rmSync(run.scratch, { recursive: true, force: true });
    }
    rmSync(origin, { recursive: true, force: true });
  }
}

test("two run snapshots preserve prior outputs/private state and admit exact uncommitted bytes", async () => {
  await fixture(async (origin, runs) => {
    mkdirSync(join(origin, ".local"));
    writeFileSync(join(origin, ".local/private.secret"), "private fixture");
    mkdirSync(join(origin, "artifacts/test-results"), { recursive: true });
    writeFileSync(join(origin, "artifacts/test-results/prior.txt"), "prior evidence");
    writeFileSync(join(origin, "uncommitted.md"), "review this exact source\n");
    const first = await createRun(origin);
    runs.push(first);
    const second = await createRun(origin);
    runs.push(second);
    assert.notEqual(first.source, second.source);
    assert.notEqual(first.results, second.results);
    assert.equal(runContext(first.source, join(first.scratch, "context.json"))?.id, first.id);
    assert.equal(
      readFileSync(join(dirname(first.results), "inputs/uncommitted.md"), "utf8"),
      "review this exact source\n",
    );
    assert.equal(
      readFileSync(join(origin, "artifacts/test-results/prior.txt"), "utf8"),
      "prior evidence",
    );
    assert.equal(readFileSync(join(origin, ".local/private.secret"), "utf8"), "private fixture");
    assert.throws(() => runContext(second.source, join(first.scratch, "context.json")));
    assert.throws(() => runContext(first.source, join(origin, "absent.json")));
  });
});

test("forged history, redirected results, changing refs/source and linked context are refused", async () => {
  await fixture(async (origin, runs) => {
    const run = await createRun(origin);
    runs.push(run);
    const path = join(run.scratch, "context.json");
    const retained = join(dirname(run.results), "run-input.json");
    const original = readFileSync(path, "utf8");
    for (const replacement of [
      { ...run, history: { ...run.history, head: "0".repeat(40) } },
      { ...run, results: join(origin, ".local") },
      { ...run, sourceSha256: "synthetic-private-context-sentinel" },
    ]) {
      writeFileSync(path, JSON.stringify(replacement));
      writeFileSync(retained, JSON.stringify(replacement));
      assert.throws(
        () => runContext(run.source, path),
        (error) =>
          error instanceof Error &&
          error.message === "Local run context is absent, inconsistent or changed." &&
          !error.message.includes("synthetic-private-context-sentinel"),
      );
    }
    writeFileSync(path, original);
    writeFileSync(retained, original);
    execFileSync("git", ["branch", "concurrent"], { cwd: origin, env: gitEnvironment() });
    assert.throws(() => runContext(run.source, path));
    execFileSync("git", ["branch", "-D", "concurrent"], { cwd: origin, env: gitEnvironment() });
    writeFileSync(join(run.source, "changed.md"), "changed snapshot");
    assert.throws(() => runContext(run.source, path));
    symlinkSync(path, join(run.scratch, "linked.json"));
    assert.throws(() => runContext(run.source, join(run.scratch, "linked.json")));
  });
});

/** @param {number[]} groups */
function processGroupsAbsent(groups) {
  if (process.platform === "win32") {
    return false;
  }
  const result = spawnSync("ps", ["-axo", "pgid="], { encoding: "utf8" });
  if (result.status !== 0) {
    return false;
  }
  const live = new Set(result.stdout.trim().split(/\s+/u).map(Number));
  return groups.every((group) => !live.has(group));
}
/** @param {()=>boolean} condition */
async function eventually(condition) {
  for (let attempt = 0; attempt < 150; attempt += 1) {
    if (condition()) {
      return;
    }
    await delay(20);
  }
  assert.fail("Owned process boundary did not reach its expected state.");
}

test("a surviving child keeps its snapshot after parent death and cannot corrupt a second run", async () => {
  await fixture(async (origin, runs) => {
    const first = await createRun(origin);
    runs.push(first);
    const log = join(first.source, "artifacts/local-ci/parent.log");
    mkdirSync(dirname(log), { recursive: true });
    const pidFile = join(first.scratch, "child.pid");
    const written = join(first.source, "artifacts/child-write.txt");
    const childScript =
      'const fs=require("node:fs"); setInterval(()=>fs.writeFileSync(process.argv[1],String(Date.now())),20);';
    const parentScript =
      'const fs=require("node:fs"); const cp=require("node:child_process"); const child=cp.spawn(process.execPath,["-e",process.argv[1],process.argv[3]],{stdio:"ignore"}); fs.writeFileSync(process.argv[2],String(child.pid)); setInterval(()=>{},1000);';
    /** @type {number[]} */
    const groups = [];
    const parent = runToLog(process.execPath, ["-e", parentScript, childScript, pidFile, written], {
      cwd: first.source,
      log,
      groups,
    });
    await eventually(() => existsSync(pidFile) && existsSync(written));
    const child = Number(readFileSync(pidFile, "utf8"));
    try {
      assert.ok(groups[0]);
      process.kill(groups[0], "SIGKILL");
      assert.equal(await parent, 128);
      assert.equal(processGroupsAbsent(groups), false);
      const second = await createRun(origin);
      runs.push(second);
      const before = readFileSync(written, "utf8");
      await delay(60);
      assert.notEqual(readFileSync(written, "utf8"), before);
      assert.equal(existsSync(join(second.source, "artifacts/child-write.txt")), false);
      await finishRun(first, { passed: false, groups });
      assert.equal(existsSync(first.source), true);
      assert.equal(existsSync(join(first.results, "admission.json")), true);
      assert.equal(existsSync(log), true);
    } finally {
      process.kill(child, "SIGTERM");
      await eventually(() => processGroupsAbsent(groups));
    }
  });
});

test("unknown profile/report bytes are not admitted and private or linked scratch cannot be cleaned", async () => {
  await fixture(async (origin, runs) => {
    const run = await createRun(origin);
    runs.push(run);
    const unknown = join(run.source, "artifacts/test-results/unit/arbitrary.json");
    mkdirSync(dirname(unknown), { recursive: true });
    writeFileSync(unknown, JSON.stringify({ profile: "synthetic private profile" }));
    assert.deepEqual(admittedReports(run), []);
    assert.throws(() => requirePassedEvidence(run, { "tests-dotnet": "passed in 1s" }));
    assert.doesNotThrow(() => requirePassedEvidence(run, { "tests-dotnet": "skipped (--skip)" }));
    assert.throws(() => removeSettledScratch(run));
    assert.equal(existsSync(unknown), true);
    const privatePath = join(run.source, ".local");
    mkdirSync(privatePath);
    writeFileSync(join(privatePath, "retained.txt"), "synthetic private state");
    assert.throws(() => removeSettledScratch(run));
    assert.equal(
      readFileSync(join(privatePath, "retained.txt"), "utf8"),
      "synthetic private state",
    );
    rmSync(privatePath, { recursive: true });
    const outside = join(origin, "artifacts/retained.txt");
    writeFileSync(outside, "outside evidence");
    symlinkSync(outside, join(run.source, "artifacts/linked.txt"));
    assert.throws(() => removeSettledScratch(run));
    assert.equal(readFileSync(outside, "utf8"), "outside evidence");
  });
});

test("a successful parent cannot authorize deletion under a detached descendant", async () => {
  await fixture(async (origin, runs) => {
    const run = await createRun(origin);
    runs.push(run);
    const log = join(run.source, "artifacts/local-ci/parent.log");
    mkdirSync(dirname(log), { recursive: true });
    const pidFile = join(run.scratch, "detached.pid");
    const written = join(run.source, "artifacts/detached-write.txt");
    const childScript =
      'const fs=require("node:fs"); setInterval(()=>fs.writeFileSync(process.argv[1],String(Date.now())),20);';
    const parentScript =
      'const fs=require("node:fs"); const cp=require("node:child_process"); const child=cp.spawn(process.execPath,["-e",process.argv[1],process.argv[3]],{stdio:"ignore",detached:true}); fs.writeFileSync(process.argv[2],String(child.pid)); child.unref();';
    /** @type {number[]} */
    const groups = [];
    assert.equal(
      await runToLog(process.execPath, ["-e", parentScript, childScript, pidFile, written], {
        cwd: run.source,
        log,
        groups,
      }),
      0,
    );
    const child = Number(readFileSync(pidFile, "utf8"));
    try {
      await eventually(() => existsSync(written));
      assert.equal(processGroupsAbsent(groups), true);
      await finishRun(run, { passed: true, groups });
      assert.equal(existsSync(run.source), true);
      const before = readFileSync(written, "utf8");
      await delay(60);
      assert.notEqual(readFileSync(written, "utf8"), before);
    } finally {
      process.kill(child, "SIGTERM");
      await eventually(() => processGroupsAbsent([child]));
    }
  });
});
