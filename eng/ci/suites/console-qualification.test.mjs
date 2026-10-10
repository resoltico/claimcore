import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { runJobs } from "./suite.mjs";
import { verifyTrx } from "./trx.mjs";

// Independently authored complete report: overflow must refuse even when report validation passes.
const report = `<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" id="11111111-1111-4111-8111-111111111111">
<Results><UnitTestResult executionId="22222222-2222-4222-8222-222222222222" testId="33333333-3333-4333-8333-333333333333" testName="synthetic" outcome="Passed" /></Results>
<TestDefinitions><UnitTest name="synthetic" id="33333333-3333-4333-8333-333333333333"><Execution id="22222222-2222-4222-8222-222222222222" /><TestMethod codeBase="Synthetic.Tests.dll" name="m" /></UnitTest></TestDefinitions>
<TestEntries><TestEntry testId="33333333-3333-4333-8333-333333333333" executionId="22222222-2222-4222-8222-222222222222" /></TestEntries>
<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary></TestRun>`;

test("native zero-exit child with a complete passing TRX cannot qualify console overflow", async (t) => {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-suite-console-"));
  const path = join(directory, "passing.trx");
  const script = join(directory, "producer.fsx");
  writeFileSync(
    script,
    `System.IO.File.WriteAllText(${JSON.stringify(path)}, ${JSON.stringify(report)})\nlet bytes = Array.zeroCreate<byte> 16777217\nSystem.Console.OpenStandardOutput().Write(bytes, 0, bytes.Length)\n`,
  );
  /** @type {import("./plan.mjs").Job} */
  const job = {
    suite: "synthetic",
    assembly: "Synthetic.Tests",
    partition: undefined,
    args: ["fsi", "--quiet", "--exec", script],
    env: {},
    results: directory,
    trx: "passing.trx",
    names: ["synthetic"],
    binaryDirectory: directory,
    privateBin: undefined,
  };
  const stdout = t.mock.method(process.stdout, "write", () => true);
  const stderr = t.mock.method(process.stderr, "write", () => true);
  try {
    const statuses = await runJobs([job], 1);
    assert.deepEqual(verifyTrx(readFileSync(path, "utf8"), job).names, ["synthetic"]);
    assert.equal(statuses.get(job), 1);
    assert.match(String(stderr.mock.calls[0]?.arguments[0]), /overflow; child exit 0/u);
  } finally {
    stdout.mock.restore();
    stderr.mock.restore();
    rmSync(directory, { recursive: true, force: true });
  }
});
