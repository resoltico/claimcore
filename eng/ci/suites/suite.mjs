import { artifactDirectory } from "../artifact-path.mjs";
import { executable } from "../executable.mjs";
// Runs registered test suites: checks the build's discovered tests against the committed inventory,
// runs the test processes (concurrently for a group), then verifies every report against the
// inventory. CI jobs and local runs use this one entry point.
//
//   node eng/ci/suites/suite.mjs run <suite-id>... | --group <group> | --cross-platform
//        [--platform linux|macos|windows] [--parallel N] [--results-root dir] [--build]
import { spawn } from "node:child_process";
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { runPlan } from "../stage-plan.mjs";
import { parseSelection, requireCompletePropertyProfile, selectSuites } from "./selection.mjs";
import { buildSuites } from "./build.mjs";
import { discoverDotnet, parseInventory } from "./inventory.mjs";
import { planSuite } from "./plan.mjs";
import { inventoryPath, loadSuites } from "./registry.mjs";
import { verifyPartitions, verifyTrx } from "./trx.mjs";

const root = fileURLToPath(new URL("../../..", import.meta.url));

const platformNames = /** @type {Record<string, string>} */ ({ win32: "windows", darwin: "macos" });

/** @returns {string} */
const currentPlatform = () => platformNames[process.platform] ?? "linux";

/**
 * @param {import("./plan.mjs").Job} job
 * @returns {Promise<{ status: number, output: string }>}
 */
function execute(job) {
  if (job.privateBin !== undefined) {
    rmSync(job.privateBin, { recursive: true, force: true });
    cpSync(job.binaryDirectory, job.privateBin, { recursive: true });
  }
  mkdirSync(job.results, { recursive: true });
  return new Promise((resolve) => {
    const child = spawn(executable("dotnet"), job.args, {
      cwd: root,
      env: { ...process.env, ...job.env },
      stdio: ["ignore", "pipe", "pipe"],
    });
    /** @type {Buffer[]} */
    const chunks = [];
    child.stdout.on("data", (chunk) => chunks.push(chunk));
    child.stderr.on("data", (chunk) => chunks.push(chunk));
    child.on("error", () => resolve({ status: 127, output: "" }));
    child.on("close", (code) => {
      if (job.privateBin !== undefined) {
        rmSync(job.privateBin, { recursive: true, force: true });
      }
      resolve({ status: code ?? 1, output: Buffer.concat(chunks).toString("utf8") });
    });
  });
}

/**
 * Run `jobs` with at most `limit` processes at a time, printing each one's output as a group.
 * @param {import("./plan.mjs").Job[]} jobs
 * @param {number} limit
 * @returns {Promise<Map<import("./plan.mjs").Job, number>>}
 */
async function runJobs(jobs, limit) {
  /** @type {Map<import("./plan.mjs").Job, number>} */
  const statuses = new Map();
  const stages = jobs.map((job, index) => ({
    id: `suite-${index}`,
    argv: ["dotnet", ...job.args],
  }));
  await runPlan({ producer: "suites", stages }, limit, async (stage) => {
    const job = jobs[stages.indexOf(stage)];
    if (!job) {
      throw new Error("The suite process plan is incomplete.");
    }
    const { status, output } = await execute(job);
    const title = job.partition ? `${job.suite} [${job.partition}]` : job.suite;
    process.stdout.write(`::group::${title}\n${output}\n::endgroup::\n`);
    statuses.set(job, status);
    return { status: status === 0 ? "passed" : "failed" };
  });
  return statuses;
}

/**
 * @param {import("./registry.mjs").Suite} suite
 * @param {import("./plan.mjs").Job[]} jobs
 * @param {string[]} names
 */
function verifyReports(suite, jobs, names) {
  const reports = jobs.map((job) => ({
    text: readFileSync(join(job.results, job.trx), "utf8"),
    names: job.names,
  }));
  const assembly = suite.assembly ?? suite.id;
  if (suite.partitions === undefined) {
    const [only] = reports;
    verifyTrx(only?.text ?? "", { assembly, names });
  } else {
    verifyPartitions(reports, { assembly, names });
  }
}

/**
 * Markdown for the job summary: the architecture graph the inspection actually observed.
 * @param {string} reportPath
 * @returns {string}
 */
export function architectureSummary(reportPath) {
  const report = JSON.parse(readFileSync(reportPath, "utf8"));
  const lines = [
    "### ArchUnitNET inspected graph",
    "",
    "| Assembly | Inspected types |",
    "|---|---:|",
  ];
  for (const item of report.assemblies) {
    lines.push(`| ${item.name} | ${item.inspectedTypes} |`);
  }
  lines.push("", "| Observed component edge |", "|---|");
  for (const edge of report.edges) {
    lines.push(`| ${edge.source} → ${edge.target} |`);
  }
  return `${lines.join("\n")}\n`;
}

/**
 * @typedef {object} Prepared
 * @property {import("./registry.mjs").Suite} suite
 * @property {import("./plan.mjs").Job[]} jobs
 * @property {string[]} names
 */

/**
 * Check the selected build against the inventory and plan the suite's processes.
 * @param {import("./registry.mjs").Suite} suite
 * @param {{ resultsRoot: string }} options
 * @returns {Prepared}
 */
function prepare(suite, { resultsRoot }) {
  if (existsSync(join(resultsRoot, suite.id))) {
    throw new Error(
      `The ${suite.id} results directory ${JSON.stringify(join(resultsRoot, suite.id))} must start absent. Preserve prior evidence and run: node eng/ci/suites/suite.mjs run ${suite.id} --results-root artifacts/results-${Date.now()}.`,
    );
  }
  const names = parseInventory(readFileSync(join(root, inventoryPath(suite)), "utf8"));
  const { names: discovered, partitions: partitionNames } = discoverDotnet(root, suite);
  if (JSON.stringify(discovered) !== JSON.stringify(names)) {
    throw new Error(`${inventoryPath(suite)} differs from the build. Run inventory.mjs --write.`);
  }
  return { suite, names, jobs: planSuite(root, suite, { resultsRoot, names, partitionNames }) };
}

/**
 * Verify a finished suite and report its outcome.
 * @param {Prepared} prepared
 * @param {Map<import("./plan.mjs").Job, number>} statuses
 * @param {string} resultsRoot
 * @returns {boolean} Whether the suite passed.
 */
function conclude({ suite, jobs, names }, statuses, resultsRoot) {
  let passed = jobs.every((job) => statuses.get(job) === 0);
  if (passed) {
    try {
      verifyReports(suite, jobs, names);
    } catch (error) {
      process.stderr.write(`${suite.id}: ${error instanceof Error ? error.message : error}\n`);
      passed = false;
    }
  }
  process.stdout.write(`Suite ${suite.id}: ${passed ? "passed" : "FAILED"}.\n`);
  const reportPath = join(resultsRoot, suite.id, "architecture-report.json");
  const summary = process.env["GITHUB_STEP_SUMMARY"];
  if (summary && existsSync(reportPath)) {
    appendFileSync(summary, architectureSummary(reportPath));
  }
  return passed;
}

/**
 * @param {string[]} argv
 * @returns {Promise<number>} Process exit code.
 */
export async function run(argv) {
  const selection = parseSelection(argv);
  const platform = selection.options["platform"] ?? currentPlatform();
  const suites = selectSuites(loadSuites(root), selection, platform);
  for (const suite of suites) {
    requireCompletePropertyProfile({ ...process.env, ...suite.env });
  }
  const limit = Number(
    selection.options["parallel"] ?? process.env["CLAIMCORE_PARALLEL_JOBS"] ?? "8",
  );
  if (!Number.isInteger(limit) || limit < 1) {
    throw new Error("Concurrency must be a positive integer.");
  }
  const resultsRoot = artifactDirectory(
    root,
    selection.options["results-root"] ?? "artifacts/test-results",
  );
  if (selection.enabled.has("build")) {
    buildSuites(root, suites);
  }
  const prepared = suites.map((suite) => prepare(suite, { resultsRoot }));
  const started = Date.now();
  const statuses = await runJobs(
    prepared.flatMap((plan) => plan.jobs),
    limit,
  );
  const outcomes = prepared.map((plan) => conclude(plan, statuses, resultsRoot));
  process.stdout.write(
    `${suites.length} suite(s) in ${Math.round((Date.now() - started) / 1000)}s.\n`,
  );
  return outcomes.every(Boolean) ? 0 : 1;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  if (process.argv[2] !== "run") {
    throw new Error("Usage: suite.mjs run <suite-id>... | --group <group>");
  }
  process.exitCode = await run(process.argv.slice(2));
}
