import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  closeSync,
  existsSync,
  ftruncateSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
  writeSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { finishRun, transferEvidence } from "./run-evidence.mjs";
import { createRun } from "./run-context.mjs";
import { admittedReports } from "./run-reports.mjs";
import { requirePassedEvidence } from "./run-expectations.mjs";
import { copySource, sourceFingerprint } from "./source-snapshot.mjs";
import { gitEnvironment } from "./scan/process.mjs";
import { writePrerequisiteReports } from "./frontend-prerequisite-fixture.mjs";
import { prerequisiteStages, finishFrontendPrerequisites } from "./frontend-prerequisites.mjs";
import { fingerprint } from "./scan/files.mjs";

/** @param {(root:string, staging:string, destination:string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-report-transfer-"));
  const staging = join(root, "qualified");
  mkdirSync(join(staging, "suite"), { recursive: true });
  writeFileSync(join(staging, "suite", "report.trx"), "synthetic qualified report\n");
  try {
    body(root, staging, join(root, "retained"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a report writer's held descriptor cannot alter its retained fresh-inode copy", () => {
  fixture((_root, staging, destination) => {
    const source = join(staging, "suite/report.trx");
    const fd = openSync(source, "r+");
    try {
      const qualified = fingerprint(staging);
      transferEvidence(staging, destination, qualified);
      const retained = join(destination, "suite/report.trx");
      const original = readFileSync(retained);
      assert.notEqual(lstatSync(retained).ino, lstatSync(source).ino);
      writeSync(fd, Buffer.from("changed by original writer"), 0, 26, 0);
      ftruncateSync(fd, 26);
      assert.notEqual(fingerprint(staging), qualified);
      assert.deepEqual(readFileSync(retained), original);
      assert.equal(fingerprint(destination), qualified);
    } finally {
      closeSync(fd);
    }
  });
});

test("changed qualified report bytes refuse before creating a retained tree", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    writeFileSync(join(staging, "suite/report.trx"), "different synthetic report");
    assert.throws(() => transferEvidence(staging, destination, qualified));
    assert.equal(existsSync(destination), false);
  });
});

test("relative fingerprints survive relocation but bind the report name", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    transferEvidence(staging, destination, qualified);
    assert.equal(fingerprint(destination), qualified);
    renameSync(join(destination, "suite/report.trx"), join(destination, "suite/other.trx"));
    assert.notEqual(fingerprint(destination), qualified);
  });
});

test("a stale retained destination is preserved and refused", () => {
  fixture((_root, staging, destination) => {
    mkdirSync(destination);
    writeFileSync(join(destination, "prior.txt"), "preserve prior evidence");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(readFileSync(join(destination, "prior.txt"), "utf8"), "preserve prior evidence");
    assert.equal(existsSync(join(destination, "suite/report.trx")), false);
  });
});

test("retained destination links and linked parents never redirect a qualified copy", () => {
  fixture((root, staging, destination) => {
    const outside = join(root, "outside");
    mkdirSync(outside);
    symlinkSync(outside, destination, "junction");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(existsSync(join(outside, "suite/report.trx")), false);
    const parent = join(root, "linked-parent");
    symlinkSync(outside, parent, "junction");
    assert.throws(() => transferEvidence(staging, join(parent, "fresh"), fingerprint(staging)));
    assert.equal(existsSync(join(outside, "fresh")), false);
  });
});

/** @param {(run:import("./run-context.mjs").RunContext)=>Promise<void>} body */
async function frontendEvidenceFixture(body) {
  const origin = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-frontend-retention-"));
  let scratch;
  try {
    copySource(resolve(import.meta.dirname, "../.."), origin);
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
    const run = await createRun(origin);
    ({ scratch } = run);
    await body(run);
  } finally {
    if (scratch) {
      rmSync(scratch, { recursive: true, force: true });
    }
    rmSync(origin, { recursive: true, force: true });
  }
}

test("passed frontend gates require a valid receipt retained with owner reports and unchanged source", async () => {
  await frontendEvidenceFixture(async (run) => {
    writePrerequisiteReports(run.source);
    const stages = { "frontend-gates": "passed in 1s" };
    assert.throws(() => requirePassedEvidence(run, stages));
    finishFrontendPrerequisites(
      run,
      prerequisiteStages.map((id) => ({
        stage: { id, argv: ["synthetic"] },
        value: { status: "passed" },
      })),
    );
    const path = join(run.source, "artifacts/frontend/prerequisites.json");
    const bytes = readFileSync(path);
    const value = JSON.parse(bytes.toString("utf8"));
    writeFileSync(path, JSON.stringify({ ...value, runId: "0".repeat(36) }));
    assert.throws(() => admittedReports(run));
    writeFileSync(path, bytes);
    assert.ok(admittedReports(run).includes(path));
    requirePassedEvidence(run, stages);
    assert.equal(sourceFingerprint(run.source), run.sourceSha256);
    await finishRun(run, { passed: true, groups: [], stages });
    assert.deepEqual(
      readFileSync(join(run.results, "artifacts/frontend/prerequisites.json")),
      bytes,
    );
    for (const leaf of [
      "artifacts/frontend/vitest-summary.json",
      "artifacts/frontend/coverage/coverage-summary.json",
      "web/artifacts/stryker/domain-mutation.json",
    ]) {
      assert.ok(existsSync(join(run.results, leaf)));
    }
    assert.equal(sourceFingerprint(join(dirname(run.results), "inputs")), run.sourceSha256);
  });
});
