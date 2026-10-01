// Runs, on this machine, the CI verification jobs that can run here, in dependency order, stopping
// before spending more time once something has failed.
//
//   node eng/ci/run-local.mjs [--all] [--changed-since REF] [--include published]
//                             [--only id,id] [--skip id,id] [--no-fail-fast]
//
// The jobs are registered in eng/ci/local-plan.json, which also records every CI family that has no
// local equivalent and why; a test holds that registry to ci.yml so it cannot drift. Each job runs
// the same command CI runs. By default a job runs only when a changed file could affect it (against
// the merge base with origin/main); when that cannot be decided, everything runs.
import { artifactDirectory, cleanDirectories } from "./artifact-path.mjs";
import { mkdirSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { affected, changedFiles } from "./local-scope.mjs";
import { flag, logTailLines, onPath, option, runToLog } from "./process-support.mjs";
import { runPlan } from "./stage-plan.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const { argv } = process;

/**
 * @typedef {object} LocalJob
 * @property {string} id
 * @property {string} title
 * @property {string[]} mirrors
 * @property {string[]} argv
 * @property {string[]} [after]
 * @property {string} [group]
 * @property {string[] | null} [scope]
 * @property {string[]} [tools]
 * @property {boolean} [optional]
 */

/**
 * @typedef {object} LocalRegistry
 * @property {string[]} [clean]
 * @property {LocalJob[]} jobs
 * @property {{ family: string, reason: string }[]} notLocal
 */

/** @param {number} started */
const seconds = (started) => `${Math.round((Date.now() - started) / 1000)}s`;

/**
 * Why a job is not run here, or null when it should run.
 * @param {LocalJob} job
 * @param {{ only: string[], skip: string[], includeOptional: boolean, changed: string[] | null }} selection
 * @returns {string | null}
 */
function reasonToSkip(job, { only, skip, includeOptional, changed }) {
  if (only.length > 0 && !only.includes(job.id)) {
    return "not selected";
  }
  if (skip.includes(job.id)) {
    return "--skip";
  }
  if (job.optional && !includeOptional) {
    return "optional; add --include published";
  }
  if (!affected(job, changed)) {
    return "no changed file affects it";
  }
  const missing = (job.tools ?? []).filter((tool) => !onPath(tool));
  return missing.length > 0 ? `needs ${missing.join(", ")} on PATH` : null;
}

/**
 * @param {LocalJob} job
 * @param {string} logs Directory receiving the job's log.
 * @param {Map<string, string>} outcomes
 * @returns {Promise<import("./types.mjs").StageResult>}
 */
async function runJob(job, logs, outcomes) {
  const log = join(logs, `${job.id}.log`);
  const started = Date.now();
  console.log(`> ${job.id}: ${job.title}`);
  const [command = "", ...args] = job.argv;
  const status = await runToLog(command, args, { cwd: root, log });
  const passed = status === 0;
  outcomes.set(job.id, `${passed ? "passed" : "FAILED"} in ${seconds(started)}`);
  console.log(
    `${passed ? "+" : "x"} ${job.id}: ${passed ? "passed" : "FAILED"} in ${seconds(started)} (${log})`,
  );
  if (!passed) {
    console.log(
      logTailLines(log, 40, 220)
        .map((line) => `    ${line}`)
        .join("\n"),
    );
  }
  return { status: passed ? "passed" : "failed" };
}

/** @param {LocalRegistry} registry @param {Map<string, string>} outcomes */
function summarize(registry, outcomes) {
  console.log("\nLocal CI summary");
  for (const job of registry.jobs) {
    console.log(`  ${job.id.padEnd(18)} ${outcomes.get(job.id) ?? "not run"}`);
  }
  console.log("Not run locally:");
  for (const item of registry.notLocal) {
    console.log(`  ${item.family.padEnd(18)} ${item.reason}`);
  }
}

/** @param {LocalRegistry} registry @returns {import("./types.mjs").Plan} */
function planOf(registry) {
  return {
    producer: "local",
    stages: registry.jobs.map(({ id, argv: command, after, group }) => ({
      id,
      argv: command,
      exclusive: true,
      ...(after === undefined ? {} : { after }),
      ...(group === undefined ? {} : { group }),
    })),
  };
}

/** @param {string[] | null} changed */
function selectionFor(changed) {
  return {
    only: option(argv, "only", "").split(",").filter(Boolean),
    skip: option(argv, "skip", "").split(",").filter(Boolean),
    includeOptional: option(argv, "include", "").split(",").includes("published"),
    changed,
  };
}

/**
 * Create this run's log directory and remove the stage outputs of earlier runs.
 * @param {LocalRegistry} registry
 * @returns {string} The log directory.
 */
function prepareRun(registry) {
  const clean = cleanDirectories(root, registry.clean ?? []);
  const logs = artifactDirectory(
    root,
    join("artifacts/local-ci", new Date().toISOString().replace(/[:.]/gu, "-")),
  );
  mkdirSync(logs, { recursive: true });
  // Stage outputs must start absent, as they do in a CI checkout; these are generated, never sources.
  for (const path of clean) {
    rmSync(path, { recursive: true, force: true });
  }
  return logs;
}

async function main() {
  const registry = /** @type {LocalRegistry} */ (
    JSON.parse(readFileSync(join(root, "eng/ci/local-plan.json"), "utf8"))
  );
  const changed = flag(argv, "all")
    ? null
    : changedFiles(root, option(argv, "changed-since", undefined));
  const selection = selectionFor(changed);
  const selectedIds = [...selection.only, ...selection.skip];
  if (selectedIds.some((id) => !registry.jobs.some((job) => job.id === id))) {
    throw new Error("Local selection names an unknown job.");
  }
  const logs = prepareRun(registry);
  // Jobs share this one working tree, and several read or write it as a whole: the documentation
  // assessment refuses a tree that changes under it, the convergence controls place probe files in
  // it, and the frontend build writes into it. In CI each job has its own checkout. Locally the jobs
  // therefore run one after another, each using the machine's cores internally.
  const parallel = Number(option(argv, "parallel", "1"));
  const outcomes = new Map();
  console.log(
    changed === null
      ? "Running every job (no change scoping)."
      : `Scoping to ${changed.length} changed file(s) against the merge base; --all runs everything.`,
  );
  const plan = planOf(registry);
  const byId = new Map(registry.jobs.map((job) => [job.id, job]));
  /** @param {import("./types.mjs").Stage} stage @returns {Promise<import("./types.mjs").StageResult>} */
  const run = (stage) => {
    const job = /** @type {LocalJob} */ (byId.get(stage.id));
    const why = reasonToSkip(job, selection);
    if (why === null) {
      return runJob(job, logs, outcomes);
    }
    outcomes.set(job.id, `skipped (${why})`);
    console.log(`- ${job.id}: skipped (${why})`);
    return Promise.resolve({ status: "skipped" });
  };
  const results = await runPlan(plan, parallel, run, { failFast: !flag(argv, "no-fail-fast") });
  for (const result of results) {
    if (result.value.status === "not-started") {
      outcomes.set(result.stage.id, "not started (an earlier job failed)");
    }
  }
  summarize(registry, outcomes);
  if (results.some((result) => result.value.status === "failed")) {
    process.exitCode = 1;
  }
}

if (argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
