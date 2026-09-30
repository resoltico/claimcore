// Runs a registered stage plan (eng/ci/stage-plans/<plan>.json) with bounded concurrency and records,
// for every stage, the same log, evidence manifest and diagnostic that a serial run records.
//
//   node eng/ci/run-stages.mjs <plan> [--parallel N] [--run-id ID] [--attempt N] [--only id,id]
//
// CI and local runs use this one entry point, so a stage cannot pass locally under a different
// command than the one that gates the merge. Without a run id the run is local.
import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { availableParallelism, tmpdir } from "node:os";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { logTailLines, onPath, option, runToLog } from "./process-support.mjs";
import { commandFor, environmentFor } from "./stage-command.mjs";
import { runPlan, validatePlan } from "./stage-plan.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const docs = join(root, "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll");
const argv = process.argv;

// A failing stage may opt in to showing the end of its own log. Only stages whose output is tool
// output about public artifacts opt in; the log is otherwise kept private (see stage-diagnostics).
/** @param {string} id @param {string} log @param {number} lines */
function echoTail(id, log, lines) {
  const text = logTailLines(log, lines, 240);
  console.log(`${id}: last ${text.length} log lines`);
  for (const line of text) console.log(`  ${line}`);
}

const timestamp = () => new Date().toISOString().replace("Z", "0000+00:00");

// Evidence manifests are registered for Linux producers; other platforms run and report only.
const recordEvidence = () =>
  option(argv, "evidence", process.platform === "linux" ? "yes" : "no") === "yes";

/**
 * Record the stage's evidence manifest and diagnostic once its command has ended.
 * @param {{ stage: import("./types.mjs").Stage, plan: import("./types.mjs").Plan, runId: string, attempt: string }} identity
 * @param {{ status: number, output: string, log: string, started: string }} run
 * @returns {import("./types.mjs").StageResult}
 */
function conclude({ stage, plan, runId, attempt }, { status, output, log, started }) {
  const outcome = status === 0 ? "success" : "failure";
  const manifest = recordEvidence()
    ? spawnSync(
        "dotnet",
        [
          docs,
          "stage-manifest",
          stage.id,
          runId,
          attempt,
          outcome,
          started,
          timestamp(),
          relative(root, output),
        ],
        { cwd: root, stdio: "inherit" },
      )
    : { status: 0 };
  if (status !== 0 && stage.echoTail) echoTail(stage.id, log, stage.echoTail);
  const report = spawnSync(
    "node",
    [
      join(root, "eng/ci/stage-report.mjs"),
      plan.producer,
      stage.id,
      String(status),
      String(manifest.status ?? 1),
      log,
    ],
    { cwd: root, stdio: "inherit" },
  );
  return { failed: status !== 0 || manifest.status !== 0 || report.status !== 0 };
}

/**
 * @param {import("./types.mjs").Stage} stage
 * @param {import("./types.mjs").Plan} plan
 * @param {string} runId
 * @param {string} attempt
 * @returns {Promise<import("./types.mjs").StageResult>}
 */
async function execute(stage, plan, runId, attempt) {
  const missing = (stage.requires ?? []).filter((tool) => !onPath(tool));
  if (missing.length > 0) {
    // CI must never skip a gate; a developer machine without the tool cannot run it.
    console.log(
      `${stage.id}: ${process.env["CI"] ? "FAILED" : "skipped"} (needs ${missing.join(", ")} on PATH).`,
    );
    return { failed: Boolean(process.env["CI"]), skipped: true };
  }
  const output = join(root, stage.output ?? `artifacts/stages/${stage.id}`);
  if (existsSync(output)) return { failed: true, note: "output already exists" };
  mkdirSync(output, { recursive: true });
  const log = join(process.env["RUNNER_TEMP"] ?? tmpdir(), `claimcore-${stage.id}.log`);
  const [command = "", ...args] = commandFor(stage, runId, root);
  const started = timestamp();
  const status = await runToLog(command, args, {
    cwd: root,
    log,
    env: environmentFor(stage, runId),
  });
  return conclude({ stage, plan, runId, attempt }, { status, output, log, started });
}

async function main() {
  const name = argv[2];
  if (!name || !/^[a-z0-9-]+$/.test(name)) throw new Error("Name a registered stage plan.");
  const plan = validatePlan(
    JSON.parse(readFileSync(join(root, `eng/ci/stage-plans/${name}.json`), "utf8")),
  );
  if (recordEvidence() && !existsSync(docs))
    throw new Error("Build the Release evidence executable first.");
  const runId = option(
    argv,
    "run-id",
    process.env["GITHUB_RUN_ID"] ?? `local-${randomUUID().replaceAll("-", "")}`,
  );
  const attempt = option(argv, "attempt", process.env["GITHUB_RUN_ATTEMPT"] ?? "1");
  const parallel = Number(option(argv, "parallel", String(Math.min(availableParallelism(), 4))));
  const only = option(argv, "only", "").split(",").filter(Boolean);
  const selected = {
    ...plan,
    stages:
      only.length === 0 ? plan.stages : plan.stages.filter((stage) => only.includes(stage.id)),
  };
  const results = await runPlan(selected, parallel, (stage) =>
    execute(stage, plan, runId, attempt),
  );
  const failed = results.filter((result) => result.value.failed).map((result) => result.stage.id);
  console.log(
    `${plan.producer}: ${results.length - failed.length} of ${results.length} stages passed.`,
  );
  if (failed.length > 0) {
    console.log(`Failed: ${failed.join(", ")}`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
