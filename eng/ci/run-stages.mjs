// Runs a registered stage plan (eng/ci/stage-plans/<plan>.json) with bounded concurrency. Each
// stage's output is captured and printed as one group when the stage ends, so concurrent stages
// never interleave; a failing stage's output is always shown.
//
//   node eng/ci/run-stages.mjs <plan> [--parallel N] [--only id,id]
//
// CI and local runs use this one entry point, so a stage cannot pass locally under a different
// command than the one that gates the merge. A stage that needs a pinned tool from
// config/tools.json installs it first; any other missing tool skips the stage on a developer
// machine and fails it in CI.
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { availableParallelism, tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { logTailLines, onPath, option, runToLog } from "./process-support.mjs";
import { commandFor, environmentFor } from "./stage-command.mjs";
import { runPlan, validatePlan } from "./stage-plan.mjs";
import { installTool, loadTools, pathWithTools } from "./tools.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const { argv } = process;
const inCi = Boolean(process.env["CI"]);
const groups = Boolean(process.env["GITHUB_ACTIONS"]);

/**
 * Make every tool a stage needs available: pinned tools are installed, any other must be on PATH.
 * @param {string[]} required
 * @returns {Promise<string[]>} The tools that remain unavailable.
 */
async function provide(required) {
  const pinned = loadTools(root);
  const missing = [];
  for (const tool of required) {
    if (tool in pinned) {
      // A pinned tool always runs at its pinned version, never at whatever else is on PATH.
      await installTool(root, tool, { tools: pinned });
    } else if (!onPath(tool)) {
      missing.push(tool);
    }
  }
  return missing;
}

/**
 * @param {string} id
 * @param {number} status
 * @param {string} log
 */
function report(id, status, log) {
  const text = readFileSync(log, "utf8");
  const tail = status === 0 ? [] : logTailLines(log, 60, 240);
  if (groups) {
    process.stdout.write(
      `::group::${id}: ${status === 0 ? "passed" : "FAILED"}\n${text}\n::endgroup::\n`,
    );
  } else if (tail.length > 0) {
    process.stdout.write(
      `${id}: last ${tail.length} log lines\n${tail.map((line) => `  ${line}`).join("\n")}\n`,
    );
  }
  process.stdout.write(`${id}: ${status === 0 ? "passed" : "FAILED"}\n`);
}

/**
 * @param {import("./types.mjs").Stage} stage
 * @param {string} runId
 * @returns {Promise<import("./types.mjs").StageResult>}
 */
async function execute(stage, runId) {
  const missing = await provide(stage.requires ?? []);
  if (missing.length > 0) {
    // CI must never skip a gate; a developer machine without the tool cannot run it.
    process.stdout.write(
      `${stage.id}: ${inCi ? "FAILED" : "skipped"} (needs ${missing.join(", ")} on PATH).\n`,
    );
    return { failed: inCi, skipped: true };
  }
  const log = join(process.env["RUNNER_TEMP"] ?? tmpdir(), `claimcore-${stage.id}.log`);
  const [command = "", ...args] = commandFor(stage, runId, root);
  const status = await runToLog(command, args, {
    cwd: root,
    log,
    env: environmentFor(stage, runId),
  });
  report(stage.id, status, log);
  return { failed: status !== 0 };
}

async function main() {
  const [, , name] = argv;
  if (!name || !/^[a-z0-9-]+$/u.test(name)) {
    throw new Error("Name a registered stage plan.");
  }
  const plan = validatePlan(
    JSON.parse(readFileSync(join(root, `eng/ci/stage-plans/${name}.json`), "utf8")),
  );
  process.env["PATH"] = pathWithTools(root);
  const runId = process.env["GITHUB_RUN_ID"] ?? `local-${randomUUID().replaceAll("-", "")}`;
  const parallel = Number(option(argv, "parallel", String(Math.min(availableParallelism(), 4))));
  const only = option(argv, "only", "").split(",").filter(Boolean);
  const selected = {
    ...plan,
    stages:
      only.length === 0 ? plan.stages : plan.stages.filter((stage) => only.includes(stage.id)),
  };
  const results = await runPlan(selected, parallel, (stage) => execute(stage, runId));
  const failed = results.filter((result) => result.value.failed).map((result) => result.stage.id);
  process.stdout.write(
    `${plan.producer}: ${results.length - failed.length} of ${results.length} stages passed.\n`,
  );
  if (failed.length > 0) {
    process.stdout.write(`Failed: ${failed.join(", ")}\n`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
