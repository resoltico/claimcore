// Runs registered test suites: checks the build's discovered tests against the committed inventory,
// runs the test processes (concurrently for a group), then verifies every report against the
// inventory. CI jobs and local runs use this one entry point.
//
//   node eng/ci/suites/suite.mjs run <suite-id>... | --group <group> | --cross-platform
//        [--platform linux|macos|windows] [--parallel N] [--results-root dir] [--build]
import { spawn, spawnSync } from "node:child_process";
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { flag, option } from "../process-support.mjs";
import { builtCliPath } from "./cli.mjs";
import { discoverDotnet, parseInventory } from "./inventory.mjs";
import { planSuite } from "./plan.mjs";
import { inventoryPath, loadSuites } from "./registry.mjs";
import { verifyPartitions, verifyTrx } from "./trx.mjs";

const root = fileURLToPath(new URL("../../..", import.meta.url));

const platformNames = /** @type {Record<string, string>} */ ({ win32: "windows", darwin: "macos" });

/** @returns {string} */
const currentPlatform = () => platformNames[process.platform] ?? "linux";

/**
 * @param {import("./registry.mjs").Suite[]} suites
 * @param {string[]} argv
 * @returns {import("./registry.mjs").Suite[]}
 */
function selectSuites(suites, argv) {
  if (flag(argv, "cross-platform")) {
    const here = option(argv, "platform", currentPlatform());
    return suites.filter(
      (suite) =>
        suite.kind === "dotnet" && suite.group === undefined && suite.platforms.includes(here),
    );
  }
  const group = option(argv, "group", undefined);
  const ids = argv.slice(argv.indexOf("run") + 1).filter((part) => !part.startsWith("--"));
  const named = group === undefined ? ids : [];
  const selected =
    group === undefined
      ? suites.filter((suite) => named.includes(suite.id))
      : suites.filter((suite) => suite.group === group);
  const missing = named.filter((id) => !suites.some((suite) => suite.id === id));
  if (selected.length === 0 || missing.length > 0) {
    throw new Error(
      `Name registered suites or a registered group (unknown: ${missing.join(", ")}).`,
    );
  }
  return selected;
}

/**
 * @param {string} command
 * @param {string[]} args
 * @returns {void}
 */
function runChecked(command, args) {
  const result = spawnSync(command, args, { cwd: root, stdio: "inherit" });
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} failed.`);
  }
}

/**
 * @param {import("./registry.mjs").Suite} suite
 * @returns {void}
 */
function build(suite) {
  const configuration = suite.configuration ?? "Release";
  for (const project of [...(suite.build ?? []), suite.project ?? ""]) {
    const extra = project === suite.project ? (suite.msbuild ?? []) : [];
    runChecked("dotnet", [
      "build",
      project,
      "--configuration",
      configuration,
      "--no-restore",
      ...extra,
    ]);
  }
}

/**
 * @param {import("./plan.mjs").Job} job
 * @param {NodeJS.ProcessEnv} extra Environment shared by every job of the run.
 * @returns {Promise<{ status: number, output: string }>}
 */
function execute(job, extra) {
  if (job.privateBin !== undefined) {
    const source = join(root, "artifacts/bin", job.assembly, "release");
    rmSync(job.privateBin, { recursive: true, force: true });
    cpSync(source, job.privateBin, { recursive: true });
  }
  mkdirSync(job.results, { recursive: true });
  return new Promise((resolve) => {
    const child = spawn("dotnet", job.args, {
      cwd: root,
      env: { ...process.env, ...extra, ...job.env },
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
 * @param {NodeJS.ProcessEnv} extra
 * @returns {Promise<Map<import("./plan.mjs").Job, number>>}
 */
async function runJobs(jobs, limit, extra) {
  /** @type {Map<import("./plan.mjs").Job, number>} */
  const statuses = new Map();
  const queue = [...jobs];
  const worker = async () => {
    for (let job = queue.shift(); job; job = queue.shift()) {
      const { status, output } = await execute(job, extra);
      const title = job.partition ? `${job.suite} [${job.partition}]` : job.suite;
      process.stdout.write(`::group::${title}\n${output}\n::endgroup::\n`);
      statuses.set(job, status);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, jobs.length) }, worker));
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
 * Build if asked, then check the build against the inventory and plan the suite's processes.
 * @param {import("./registry.mjs").Suite} suite
 * @param {{ build: boolean, resultsRoot: string }} options
 * @returns {Prepared}
 */
function prepare(suite, { build: wantBuild, resultsRoot }) {
  if (wantBuild) {
    build(suite);
  }
  const names = parseInventory(readFileSync(join(root, inventoryPath(suite)), "utf8"));
  const { names: discovered, partitions: partitionNames } = discoverDotnet(root, suite);
  if (JSON.stringify(discovered) !== JSON.stringify(names)) {
    throw new Error(`${inventoryPath(suite)} differs from the build. Run inventory.mjs --write.`);
  }
  if (existsSync(join(resultsRoot, suite.id))) {
    throw new Error(`The ${suite.id} results directory must start absent.`);
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
  const platform = option(argv, "platform", currentPlatform());
  // A named suite runs wherever it is asked to; a group runs only the members for this platform.
  const grouped = option(argv, "group", undefined) !== undefined;
  const suites = selectSuites(loadSuites(root), argv).filter(
    (suite) => suite.kind === "dotnet" && (!grouped || suite.platforms.includes(platform)),
  );
  const limit = Number(option(argv, "parallel", process.env["CLAIMCORE_PARALLEL_JOBS"] ?? "8"));
  const resultsRoot = join(root, option(argv, "results-root", "artifacts/test-results"));
  const prepared = suites.map((suite) =>
    prepare(suite, { build: flag(argv, "build"), resultsRoot }),
  );
  const started = Date.now();
  const needsCli = suites.some((suite) => suite.group === "postgres");
  const extra =
    needsCli && process.env["CLAIMCORE_TEST_CLI_PATH"] === undefined
      ? { CLAIMCORE_TEST_CLI_PATH: builtCliPath() }
      : {};
  const statuses = await runJobs(
    prepared.flatMap((plan) => plan.jobs),
    limit,
    extra,
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
