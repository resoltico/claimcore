// Holds every workflow and runner that repeats a test-suite fact to config/test-suites.json.
//
// The registry owns each suite's assembly, project, exact minimum test count and coverage role.
// A static GitHub Actions matrix cannot read it, so this check compares the literals instead, and
// the PostgreSQL qualification runner reads the registry directly and must not repeat the table.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseWorkflow } from "./yaml.mjs";

/**
 * @typedef {object} Suite
 * @property {string} id
 * @property {string} [stage]
 * @property {string} assembly
 * @property {string} project
 * @property {number} expected
 * @property {boolean} coverage
 * @property {string} [timeout]
 * @property {string[]} platforms
 */

/**
 * @param {string} root
 * @param {string} path
 */
const read = (root, path) => readFileSync(join(root, path), "utf8");

/**
 * Whether one matrix entry repeats its suite's registered facts exactly.
 * @param {Suite} suite
 * @param {import("./types.mjs").Json} entry
 */
function entryMatches(suite, entry) {
  return (
    entry["stage"] === `${suite.id}-${entry["platform"]}` &&
    entry["project"] === suite.project &&
    Number(entry["expected"]) === suite.expected &&
    (entry["coverage"] === true || entry["coverage"] === "true") === suite.coverage &&
    suite.platforms.includes(entry["platform"])
  );
}

/**
 * @param {Suite[]} suites
 * @param {import("./types.mjs").Json[]} include
 * @returns {string[]}
 */
function checkEntries(suites, include) {
  /** @type {string[]} */
  const errors = [];
  for (const entry of include) {
    const suite = suites.find((candidate) => candidate.assembly === entry["assembly"]);
    if (suite === undefined) {
      errors.push(`verify-unit.yml runs unregistered assembly ${entry["assembly"]}.`);
    } else if (!entryMatches(suite, entry)) {
      errors.push(
        `verify-unit.yml entry ${entry["stage"]} disagrees with config/test-suites.json.`,
      );
    }
  }
  return errors;
}

/**
 * The static verify-unit matrix, entry by entry.
 * @param {Suite[]} suites
 * @param {string} workflow
 * @returns {string[]}
 */
function checkUnitMatrix(suites, workflow) {
  const parsed = parseWorkflow(workflow).value;
  const include = parsed["jobs"]?.["tests"]?.["strategy"]?.["matrix"]?.["include"];
  if (!Array.isArray(include)) {
    return ["verify-unit.yml has no test matrix."];
  }
  const seen = new Set(include.map((entry) => entry["stage"]));
  const missing = suites
    .filter((suite) => suite.stage === undefined && suite.id !== "docs")
    .flatMap((suite) => suite.platforms.map((platform) => `${suite.id}-${platform}`))
    .filter((stage) => !seen.has(stage))
    .map((stage) => `verify-unit.yml does not run ${stage}.`);
  return [...checkEntries(suites, include), ...missing];
}

/**
 * @param {Suite[]} suites
 * @param {string} source
 * @param {string} name
 * @param {string[]} ids
 * @returns {string[]}
 */
function checkMinimums(suites, source, name, ids) {
  const found = [...source.matchAll(/--minimum-expected-tests=(\d+)/gu)].map((match) =>
    Number(match[1]),
  );
  const wanted = ids.map((id) => suites.find((suite) => suite.id === id)?.expected);
  return JSON.stringify(found) === JSON.stringify(wanted)
    ? []
    : [`${name} repeats minimum counts ${found.join(",")}; the registry says ${wanted.join(",")}.`];
}

/**
 * @param {string} root
 * @returns {string[]}
 */
export function checkTestSuites(root) {
  /** @type {{ suites: Suite[] }} */
  const registry = JSON.parse(read(root, "config/test-suites.json"));
  const { suites } = registry;
  /** @type {string[]} */
  const errors = [];
  const ids = new Set();
  for (const suite of suites) {
    if (ids.has(suite.id)) {
      errors.push(`Suite ${suite.id} is registered twice.`);
    }
    ids.add(suite.id);
    if (!Number.isInteger(suite.expected) || suite.expected < 1) {
      errors.push(`Suite ${suite.id} needs a positive exact test count.`);
    }
  }
  errors.push(
    ...checkUnitMatrix(suites, read(root, ".github/workflows/verify-unit.yml")),
    ...checkMinimums(
      suites,
      read(root, ".github/workflows/verify-properties.yml"),
      "verify-properties.yml",
      ["unit", "fuzz"],
    ),
    ...checkMinimums(
      suites,
      read(root, ".github/workflows/verify-documentation.yml"),
      "verify-documentation.yml",
      ["docs"],
    ),
  );
  const partitions = JSON.parse(read(root, "eng/test-partitions.json"));
  for (const assembly of partitions.assemblies) {
    const suite = suites.find((candidate) => candidate.assembly === assembly.assembly);
    const total = assembly.partitions.reduce(
      (/** @type {number} */ sum, /** @type {{ tests: number }} */ part) => sum + part.tests,
      0,
    );
    if (suite === undefined || suite.expected !== total) {
      errors.push(`Partitions of ${assembly.assembly} do not sum to the registered count.`);
    }
  }
  if (/\bExpected\s*=\s*\d+/u.test(read(root, "eng/Invoke-PostgresQualifications.ps1"))) {
    errors.push(
      "Invoke-PostgresQualifications.ps1 repeats a test count instead of reading the registry.",
    );
  }
  return errors;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = fileURLToPath(new URL("../..", import.meta.url));
  const errors = checkTestSuites(root);
  for (const error of errors) {
    process.stderr.write(`${error}\n`);
  }
  process.stdout.write(
    errors.length === 0 ? "Test-suite registry matches every workflow and runner.\n" : "",
  );
  process.exitCode = errors.length === 0 ? 0 : 1;
}
