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
import { spawn, spawnSync } from "node:child_process";
import { closeSync, mkdirSync, openSync, readFileSync, rmSync } from "node:fs";
import { delimiter, join } from "node:path";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { runPlan } from "./stage-plan.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));

function option(name, fallback) {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 && process.argv[at + 1] !== undefined ? process.argv[at + 1] : fallback;
}
const flag = (name) => process.argv.includes(`--${name}`);

const git = (...args) =>
  spawnSync("git", args, {
    cwd: root,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
  });

/** Files changed against `ref`, including uncommitted and untracked ones; null when unknown. */
export function changedFiles(ref) {
  const base = ref ?? git("merge-base", "HEAD", "origin/main").stdout.trim();
  if (!base) return null;
  const tracked = git("diff", "--name-only", base);
  const untracked = git("ls-files", "--others", "--exclude-standard");
  if (tracked.status !== 0 || untracked.status !== 0) return null;
  return [...tracked.stdout.split("\n"), ...untracked.stdout.split("\n")].filter(Boolean);
}

/** Whether `job` must run given the changed files (null means unknown, so it must). */
export function affected(job, changed) {
  if (job.scope === null || job.scope === undefined || changed === null) return true;
  const patterns = job.scope.map((pattern) => new RegExp(pattern));
  return changed.some((file) => patterns.some((pattern) => pattern.test(file)));
}

const onPath = (tool) =>
  (process.env.PATH ?? "")
    .split(delimiter)
    .some((directory) => directory !== "" && existsSync(join(directory, tool)));

function tail(path, lines) {
  return readFileSync(path, "utf8")
    .replace(/\u001b\[[0-9;]*m/g, "")
    .split("\n")
    .filter((line) => line.trim() !== "")
    .slice(-lines)
    .map((line) => `    ${line.slice(0, 220)}`)
    .join("\n");
}

const seconds = (started) => `${Math.round((Date.now() - started) / 1000)}s`;

async function main() {
  const registry = JSON.parse(readFileSync(join(root, "eng/ci/local-plan.json"), "utf8"));
  const only = option("only", "").split(",").filter(Boolean);
  const skip = option("skip", "").split(",").filter(Boolean);
  const includeOptional = option("include", "").split(",").includes("published");
  const changed = flag("all") ? null : changedFiles(option("changed-since", undefined));
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const logs = join(root, "artifacts/local-ci", stamp);
  mkdirSync(logs, { recursive: true });
  // Stage outputs must start absent, as they do in a CI checkout; these are generated, never sources.
  for (const path of registry.clean ?? [])
    rmSync(join(root, path), { recursive: true, force: true });
  // Jobs share this one working tree, and several read or write it as a whole: the documentation
  // assessment refuses a tree that changes under it, the analyzer and convergence controls place probe
  // files in it, and the frontend build writes into it. In CI each job has its own checkout. Locally the
  // jobs therefore run one after another, each using the machine's cores internally.
  const parallel = Number(option("parallel", "1"));
  const outcomes = new Map();

  console.log(
    changed === null
      ? "Running every job (no change scoping)."
      : `Scoping to ${changed.length} changed file(s) against the merge base; --all runs everything.`,
  );

  const plan = {
    producer: "local",
    stages: registry.jobs.map((job) => ({
      id: job.id,
      argv: job.argv,
      after: job.after,
      group: job.group,
    })),
  };
  const byId = new Map(registry.jobs.map((job) => [job.id, job]));

  const run = (stage) => {
    const job = byId.get(stage.id);
    const skipped = (why) => {
      outcomes.set(job.id, `skipped (${why})`);
      console.log(`- ${job.id}: skipped (${why})`);
      return { failed: false, skipped: true };
    };
    if (only.length > 0 && !only.includes(job.id)) return skipped("not selected");
    if (skip.includes(job.id)) return skipped("--skip");
    if (job.optional && !includeOptional) return skipped("optional; add --include published");
    if (!affected(job, changed)) return skipped("no changed file affects it");
    const missing = (job.tools ?? []).filter((tool) => !onPath(tool));
    if (missing.length > 0) return skipped(`needs ${missing.join(", ")} on PATH`);

    const log = join(logs, `${job.id}.log`);
    const descriptor = openSync(log, "w");
    const started = Date.now();
    console.log(`> ${job.id}: ${job.title}`);
    return new Promise((resolve) => {
      const [command, ...args] = job.argv;
      const child = spawn(command, args, {
        cwd: root,
        stdio: ["ignore", descriptor, descriptor],
      });
      const settle = (status) => {
        closeSync(descriptor);
        const passed = status === 0;
        outcomes.set(job.id, `${passed ? "passed" : "FAILED"} in ${seconds(started)}`);
        console.log(
          `${passed ? "+" : "x"} ${job.id}: ${passed ? "passed" : "FAILED"} in ${seconds(started)} (${log})`,
        );
        if (!passed) console.log(tail(log, 40));
        resolve({ failed: !passed });
      };
      child.on("error", () => settle(127));
      child.on("close", (code, signal) => settle(code ?? (signal ? 128 : 1)));
    });
  };

  const results = await runPlan(plan, parallel, run, {
    failFast: !flag("no-fail-fast"),
  });
  for (const result of results)
    if (result.value.notStarted)
      outcomes.set(result.stage.id, "not started (an earlier job failed)");

  console.log("\nLocal CI summary");
  for (const job of registry.jobs)
    console.log(`  ${job.id.padEnd(18)} ${outcomes.get(job.id) ?? "not run"}`);
  console.log("Not run locally:");
  for (const item of registry.notLocal) console.log(`  ${item.family.padEnd(18)} ${item.reason}`);
  if (results.some((result) => result.value.failed)) process.exitCode = 1;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
