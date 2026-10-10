import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  renameSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { createRun } from "./run-context.mjs";
import { finishRun } from "./run-evidence.mjs";
import { retainRunDiagnostic } from "./run-diagnostics.mjs";
import { runToLog } from "./process-support.mjs";
import { copySource } from "./source-snapshot.mjs";
import { gitEnvironment } from "./scan/process.mjs";

/** @param {(origin:string,runs:import("./run-context.mjs").RunContext[])=>Promise<void>} body */
async function fixture(body) {
  const origin = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-outcome-"));
  /** @type {import("./run-context.mjs").RunContext[]} */
  const runs = [];
  try {
    copySource(resolve(import.meta.dirname, "../.."), origin);
    for (const args of [
      ["init", "-b", "main"],
      ["config", "commit.gpgsign", "false"],
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

test("failed execution survives refused admission in an independently owned diagnostic destination", async () => {
  await fixture(async (origin, runs) => {
    const run = await createRun(origin);
    runs.push(run);
    const log = join(run.source, "artifacts/failed-child.log");
    mkdirSync(dirname(log), { recursive: true });
    assert.equal(
      await runToLog(process.execPath, ["-e", "process.exitCode=7"], { cwd: run.source, log }),
      7,
    );
    const path = join(run.scratch, "context.json");
    const outside = join(origin, "outside-results");
    const rejected = JSON.stringify({ ...run, results: outside });
    writeFileSync(path, rejected);
    writeFileSync(join(dirname(run.results), "run-input.json"), rejected);
    await assert.rejects(
      finishRun(run, { passed: false, groups: [], stages: { command: "exit 7" } }),
    );
    const retained = JSON.parse(
      readFileSync(join(dirname(run.results), "diagnostics/outcome-refused.json"), "utf8"),
    );
    assert.equal(retained.execution.stages.command, "exit 7");
    assert.equal(retained.admission, "refused");
    assert.equal(existsSync(outside), false);
    assert.equal(existsSync(log), true);
  });
});

test("diagnostic directory replacement preserves natural failure despite capture refusal", async (t) => {
  await fixture(async (origin, runs) => {
    const run = await createRun(origin);
    runs.push(run);
    const directory = join(dirname(run.results), "diagnostics");
    const preserved = `${directory}-preserved`;
    renameSync(directory, preserved);
    mkdirSync(directory, { mode: 0o700 });
    renameSync(join(preserved, "context.json"), join(directory, "context.json"));
    const log = join(run.source, "artifacts/overflow-child.log");
    mkdirSync(dirname(log), { recursive: true });
    /** @type {Record<string,string>} */
    const stages = {};
    assert.equal(
      await runToLog(
        process.execPath,
        ["-e", "process.stdout.write(Buffer.alloc(16777217)); process.exitCode=7"],
        {
          cwd: run.source,
          log,
          onOutcome: ({ exit, captureFailed }) => {
            stages["command-execution"] = `exit ${exit}; captureFailed=${captureFailed}`;
          },
        },
      ),
      7,
    );
    const stderr = t.mock.method(process.stderr, "write", () => true);
    try {
      assert.equal(retainRunDiagnostic(run, { passed: false, stages }, "refused"), null);
      assert.match(
        String(stderr.mock.calls[0]?.arguments[0]),
        /command-execution.*exit 7; captureFailed=true/u,
      );
      assert.equal(existsSync(log), true);
    } finally {
      stderr.mock.restore();
    }
    assert.equal(existsSync(join(directory, "outcome-refused.json")), false);
  });
});
