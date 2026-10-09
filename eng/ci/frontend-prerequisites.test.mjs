import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { createRun, contextEnvironment } from "./run-context.mjs";
import {
  mutationReport,
  writeJson,
  writePrerequisiteReports,
} from "./frontend-prerequisite-fixture.mjs";
import { frontendReports } from "./run-frontend-reports.mjs";
import { copySource } from "./source-snapshot.mjs";
import { gitEnvironment } from "./scan/process.mjs";
import { targets } from "../../web/scripts/check-mutation-report.mjs";
import {
  prerequisiteStages,
  beginFrontendPrerequisites,
  finishFrontendPrerequisites,
  ensureFrontendPrerequisites,
  verifyFrontendPrerequisites,
} from "./frontend-prerequisites.mjs";

const root = resolve(import.meta.dirname, "../..");
let origin = "";
/** @type {import("./run-context.mjs").RunContext | undefined} */
let context;
const previousContext = process.env["CLAIMCORE_RUN_CONTEXT"];
/** @returns {import("./run-context.mjs").RunContext} */
function run() {
  assert.ok(context);
  return context;
}
const receipt = () => join(run().source, "artifacts/frontend/prerequisites.json");
function produce() {
  beginFrontendPrerequisites(run().source, prerequisiteStages);
  finishFrontendPrerequisites(
    run(),
    prerequisiteStages.map((id) => ({
      stage: { id, argv: ["synthetic"] },
      value: { status: "passed" },
    })),
  );
}

before(async () => {
  origin = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-frontend-prerequisites-"));
  copySource(root, origin);
  const stages = prerequisiteStages.map((id) => ({
    id,
    argv: [
      process.execPath,
      "-e",
      "process.exit(process.env.CLAIMCORE_SYNTHETIC_STAGE_FAILURE === '1' && process.argv[1] === 'frontend-unit' ? 1 : 0)",
      id,
    ],
  }));
  writeFileSync(
    join(origin, "eng/ci/stage-plans/frontend.json"),
    JSON.stringify({ producer: "frontend", stages }),
  );
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
  context = await createRun(origin);
  process.env["CLAIMCORE_RUN_CONTEXT"] = join(context.scratch, "context.json");
  writePrerequisiteReports(run().source);
});
after(() => {
  if (previousContext === undefined) {
    delete process.env["CLAIMCORE_RUN_CONTEXT"];
  } else {
    process.env["CLAIMCORE_RUN_CONTEXT"] = previousContext;
  }
  if (context) {
    rmSync(context.scratch, { recursive: true, force: true });
  }
  rmSync(origin, { recursive: true, force: true });
});

test("a complete same-run receipt reuses prerequisites without executing any command", () => {
  produce();
  let calls = 0;
  ensureFrontendPrerequisites(run().source, () => {
    calls += 1;
    return 1;
  });
  assert.equal(calls, 0);
  assert.equal(JSON.parse(readFileSync(receipt(), "utf8")).runId, run().id);
});

test("absence executes prerequisites once; nonzero actual stage exit cannot certify valid reports", () => {
  const environment = contextEnvironment(run());
  const summary = readFileSync(join(run().source, "artifacts/frontend/vitest-summary.json"));
  const failed = spawnSync(
    process.execPath,
    ["eng/ci/run-stages.mjs", "frontend", "--only", prerequisiteStages.join(",")],
    {
      cwd: run().source,
      env: { ...environment, CLAIMCORE_SYNTHETIC_STAGE_FAILURE: "1" },
      encoding: "utf8",
    },
  );
  assert.equal(failed.status, 1, failed.stderr);
  assert.match(failed.stdout, /frontend-unit: FAILED/u);
  assert.match(failed.stdout, /frontend: 2 passed, 1 failed, 0 skipped/u);
  assert.deepEqual(
    readFileSync(join(run().source, "artifacts/frontend/vitest-summary.json")),
    summary,
  );
  assert.equal(existsSync(receipt()), false);
  let calls = 0;
  ensureFrontendPrerequisites(run().source, () => {
    calls += 1;
    const completed = spawnSync(
      process.execPath,
      ["eng/ci/run-stages.mjs", "frontend", "--only", prerequisiteStages.join(",")],
      { cwd: run().source, env: environment, encoding: "utf8" },
    );
    for (const id of prerequisiteStages) {
      assert.ok(completed.stdout.includes(`${id}: passed`), completed.stderr);
    }
    return completed.status;
  });
  assert.equal(calls, 1);
  verifyFrontendPrerequisites(run());
});

test("a selected rerun invalidates its receipt while unrelated stages preserve it", () => {
  const bytes = readFileSync(receipt(), "utf8");
  beginFrontendPrerequisites(run().source, ["frontend-format"]);
  assert.equal(readFileSync(receipt(), "utf8"), bytes);
  beginFrontendPrerequisites(run().source, ["frontend-unit"]);
  assert.equal(existsSync(receipt()), false);
  for (const status of ["failed", "skipped", "not-started"]) {
    finishFrontendPrerequisites(
      run(),
      prerequisiteStages.map((id) => ({
        stage: { id, argv: ["synthetic"] },
        value: /** @type {import("./types.mjs").StageResult} */ ({
          status: id === "frontend-unit" ? status : "passed",
        }),
      })),
    );
    assert.equal(existsSync(receipt()), false);
  }
  finishFrontendPrerequisites(run(), []);
  assert.equal(existsSync(receipt()), false);
  produce();
});

test("foreign run, source, producing inputs, extra members and malformed receipts refuse without execution", () => {
  const bytes = readFileSync(receipt(), "utf8");
  const value = JSON.parse(bytes);
  const invalid = [
    { ...value, runId: "0".repeat(36) },
    { ...value, sourceSha256: "0".repeat(64) },
    { ...value, producingInputsSha256: "0".repeat(64) },
    { ...value, extra: true },
    null,
  ];
  let calls = 0;
  const execute = () => {
    calls += 1;
    return 0;
  };
  for (const candidate of invalid) {
    writeFileSync(receipt(), JSON.stringify(candidate));
    assert.throws(() => ensureFrontendPrerequisites(run().source, execute));
  }
  for (const text of ["{", " ".repeat(4097)]) {
    writeFileSync(receipt(), text);
    assert.throws(() => ensureFrontendPrerequisites(run().source, execute));
  }
  assert.equal(calls, 0);
  writeFileSync(receipt(), bytes);
});

test("tampered, missing or linked reports and linked receipts refuse without regenerating", () => {
  const leaves = [
    "artifacts/frontend/vitest-summary.json",
    "artifacts/frontend/coverage/coverage-summary.json",
    "web/artifacts/stryker/domain-mutation.json",
  ];
  let calls = 0;
  const execute = () => {
    calls += 1;
    return 0;
  };
  const check = () => assert.throws(() => ensureFrontendPrerequisites(run().source, execute));
  for (const leaf of leaves) {
    const path = join(run().source, leaf);
    const bytes = readFileSync(path);
    writeFileSync(path, Buffer.concat([bytes, Buffer.from("\n")]));
    check();
    writeFileSync(path, Buffer.alloc(16 * 1024 * 1024 + 1));
    check();
    rmSync(path);
    check();
    const target = join(run().scratch, "outside-report.json");
    writeFileSync(target, bytes);
    symlinkSync(target, path);
    check();
    rmSync(path);
    writeFileSync(path, bytes);
  }
  const bytes = readFileSync(receipt());
  const target = join(run().scratch, "outside-receipt.json");
  writeFileSync(target, bytes);
  rmSync(receipt());
  symlinkSync(target, receipt());
  check();
  rmSync(receipt());
  writeFileSync(receipt(), bytes);
  assert.equal(calls, 0);
});

test("owner admission refuses incomplete mutation and false test inventory before certifying", () => {
  beginFrontendPrerequisites(run().source, prerequisiteStages);
  const mutation = mutationReport(run().source);
  const [first] = targets;
  assert.ok(first);
  const file = mutation.files[first];
  assert.ok(file);
  file.mutants[0] = { status: "Timeout" };
  writeJson(run().source, "web/artifacts/stryker/domain-mutation.json", mutation);
  assert.throws(produce);
  assert.equal(existsSync(receipt()), false);
  writePrerequisiteReports(run().source);
  const path = join(run().source, "artifacts/frontend/vitest-summary.json");
  const vitest = JSON.parse(readFileSync(path, "utf8"));
  vitest.tests.pop();
  writeFileSync(path, JSON.stringify(vitest));
  assert.throws(produce);
  assert.equal(existsSync(receipt()), false);
  writePrerequisiteReports(run().source);
  produce();
});

test("invalid stage selection preserves a valid receipt and changed source refuses reuse", () => {
  const bytes = readFileSync(receipt());
  const invalid = spawnSync(
    process.execPath,
    ["eng/ci/run-stages.mjs", "frontend", "--only", "frontend-unknown"],
    { cwd: run().source, env: contextEnvironment(run()), encoding: "utf8" },
  );
  assert.equal(invalid.status, 1);
  assert.deepEqual(readFileSync(receipt()), bytes);
  const path = join(run().source, "README.md");
  const source = readFileSync(path);
  writeFileSync(path, Buffer.concat([source, Buffer.from("\nchanged source\n")]));
  try {
    assert.throws(() =>
      ensureFrontendPrerequisites(run().source, () => assert.fail("must not run")),
    );
  } finally {
    writeFileSync(path, source);
  }
  verifyFrontendPrerequisites(run());
});

test("unrelated failed browser evidence is preserved without invalidating frontend prerequisite reuse", () => {
  const path = join(run().source, "artifacts/browser/chromium.json");
  const bytes = '{"status":"failed","failureCodes":["SYNTHETIC_BROWSER_FAILURE"]}';
  writeJson(run().source, "artifacts/browser/chromium.json", JSON.parse(bytes));
  try {
    ensureFrontendPrerequisites(run().source, () => assert.fail("must reuse frontend results"));
    assert.equal(readFileSync(path, "utf8"), bytes);
    assert.throws(
      () => frontendReports(run().source),
      "full browser evidence must still fail admission",
    );
  } finally {
    rmSync(path);
  }
});
