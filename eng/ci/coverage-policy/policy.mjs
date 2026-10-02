// Coverage rules: which Cobertura reports must exist, the floors a merged report must meet and the
// evidence a browser run must contain. Reports are read with the strict XML reader; a doctype that
// only names an external DTD (ReportGenerator writes one) is tolerated, never resolved.
import { readdirSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { productionAssemblies, reconcileMeasurements } from "./measurement.mjs";
import { descendantsNamed, parseXml } from "../suites/xml.mjs";

export const browserEngines = ["chromium", "firefox", "webkit"];
export const mergedFloors = { line: 0.6, branch: 0.4 };
export const webPackageFloors = { line: 0.8, branch: 0.7 };

/**
 * @param {string} path
 * @returns {import("../suites/xml.mjs").XmlElement}
 */
function readCoverage(path) {
  const root = parseXml(readFileSync(path, "utf8"), { allowDoctype: true });
  if (root.name !== "coverage") {
    throw new Error("Coverage must be a Cobertura coverage document.");
  }
  return root;
}

/**
 * @param {import("../suites/xml.mjs").XmlElement} node
 * @param {string} name
 * @param {{ minimum: number, subject: string }} rule
 * @returns {number}
 */
function rate(node, name, { minimum, subject }) {
  const value = Number(node.attributes[name]);
  if (node.attributes[name] === undefined || !Number.isFinite(value) || value < 0 || value > 1) {
    throw new Error(`${subject} has an invalid ${name} value.`);
  }
  if (value < minimum) {
    throw new Error(
      `${subject} ${name} is below the required ${Math.round(minimum * 100)}% floor.`,
    );
  }
  return value;
}

/**
 * The merged report must meet the repository floors and carry every ClaimCore.Web package above its own.
 * @param {string} path
 * @param {string[]} [expected]
 * @returns {{ lineRate: number, branchRate: number, webPackages: number }}
 */
export function checkFloors(path, expected = productionAssemblies()) {
  const root = readCoverage(path);
  const measured = reconcileMeasurements(root, expected);
  const derivedRate = (/** @type {number} */ covered, /** @type {number} */ valid) =>
    valid === 0 ? 1 : covered / valid;
  if (
    derivedRate(measured.total.coveredLines, measured.total.lines) < mergedFloors.line ||
    derivedRate(measured.total.coveredBranches, measured.total.branches) < mergedFloors.branch
  ) {
    throw new Error("Measured production coverage is below the required floor.");
  }
  for (const [name, counts] of measured.packages) {
    if (
      name.startsWith("ClaimCore.Web") &&
      (derivedRate(counts.coveredLines, counts.lines) < webPackageFloors.line ||
        derivedRate(counts.coveredBranches, counts.branches) < webPackageFloors.branch)
    ) {
      throw new Error("Measured Web coverage is below the required floor.");
    }
  }
  const subject = "Merged production coverage";
  const lineRate = rate(root, "line-rate", { minimum: mergedFloors.line, subject });
  const branchRate = rate(root, "branch-rate", { minimum: mergedFloors.branch, subject });
  const packages = descendantsNamed(root, "package").filter((item) =>
    (item.attributes["name"] ?? "").startsWith("ClaimCore.Web"),
  );
  if (packages.length === 0) {
    throw new Error("Merged coverage contains no ClaimCore.Web production package.");
  }
  for (const item of packages) {
    const label = "ClaimCore.Web package";
    rate(item, "line-rate", { minimum: webPackageFloors.line, subject: label });
    rate(item, "branch-rate", { minimum: webPackageFloors.branch, subject: label });
  }
  return { lineRate, branchRate, webPackages: packages.length };
}

/** @param {string | undefined} text */
function wholeNumber(text) {
  const value = text !== undefined && /^[0-9]+$/u.test(text) ? Number(text) : Number.NaN;
  return Number.isSafeInteger(value) ? value : Number.NaN;
}

/**
 * The branches a package's lines actually measured.
 * @param {import("../suites/xml.mjs").XmlElement} pkg
 * @returns {number}
 */
function measuredBranches(pkg) {
  let measured = 0;
  for (const line of descendantsNamed(pkg, "line")) {
    if (line.attributes["branch"]?.toLowerCase() !== "true") {
      continue;
    }
    const match = /\(([0-9]+)\/([0-9]+)\)$/u.exec(line.attributes["condition-coverage"] ?? "");
    const [coveredHere, validHere] = [wholeNumber(match?.[1]), wholeNumber(match?.[2])];
    if (!match || !(coveredHere >= 0 && validHere >= 1 && coveredHere <= validHere)) {
      throw new Error("Coverage has invalid measured production branch evidence.");
    }
    measured += coveredHere;
  }
  return measured;
}

/**
 * The named production package, whose branch rate must be a real measurement.
 * @param {import("../suites/xml.mjs").XmlElement} root
 * @param {string} name
 * @returns {import("../suites/xml.mjs").XmlElement}
 */
function measuredPackage(root, name) {
  const packages = descendantsNamed(root, "package").filter(
    (item) => item.attributes["name"] === name,
  );
  const [only] = packages;
  if (packages.length !== 1 || !only) {
    throw new Error(`Coverage must contain exactly one ${name} production package.`);
  }
  const branchRate = Number(only.attributes["branch-rate"]);
  if (!(Number.isFinite(branchRate) && branchRate > 0 && branchRate <= 1)) {
    throw new Error(`Coverage must measure ${name} production branches.`);
  }
  return only;
}

/**
 * A browser run's report must measure ClaimCore.Web branches, not merely exist.
 * @param {string} path
 */
export function checkBrowserCoverage(path) {
  const root = readCoverage(path);
  const covered = wholeNumber(root.attributes["branches-covered"]);
  const valid = wholeNumber(root.attributes["branches-valid"]);
  if (!(covered >= 1 && valid >= covered)) {
    throw new Error("Browser coverage must contain measured branch counters.");
  }
  if (measuredBranches(measuredPackage(root, "ClaimCore.Web")) < 1) {
    throw new Error("Browser coverage must contain measured ClaimCore.Web branch evidence.");
  }
}

/** Published CLI execution must contribute actual CLI entry-process branches.
 * @param {string} path
 */
export function checkCliCoverage(path) {
  const root = readCoverage(path);
  if (measuredBranches(measuredPackage(root, "ClaimCore.Cli")) < 1) {
    throw new Error("CLI coverage must contain measured entry-process branch evidence.");
  }
}

/**
 * @param {string} directory
 * @returns {string[]} Cobertura files below `directory`, sorted.
 */
function coverageFiles(directory) {
  return readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile() && /\.coverage\.cobertura\..*\.xml$/u.test(entry.name))
    .map((entry) => join(entry.parentPath, entry.name))
    .sort();
}

/**
 * The file prefixes the registered suites' measured processes write: the suite id, or the suite id
 * and partition for a partitioned suite.
 * @param {import("../suites/registry.mjs").Suite[]} suites
 * @returns {string[]}
 */
export const measuredPrefixes = (suites) =>
  suites
    .filter((suite) => suite.kind === "dotnet" && suite.coverage === true)
    .flatMap((suite) =>
      suite.partitions === undefined
        ? [suite.id]
        : suite.partitions.ids.map((id) => `${suite.id}-${id}`),
    );

/**
 * Find the one report each measured process must have written, wherever the artifacts were
 * unpacked below `root`, and the report of each browser engine. Nothing else may be present.
 * @param {string} root
 * @param {import("../suites/registry.mjs").Suite[]} suites
 * @returns {string[]} The reports to merge.
 */
export function resolveInputs(root, suites) {
  const everything = coverageFiles(root);
  /** @type {string[]} */
  const reports = [];
  for (const prefix of measuredPrefixes(suites)) {
    const found = everything.filter((path) =>
      basename(path).startsWith(`${prefix}.coverage.cobertura.`),
    );
    const [one] = found;
    if (found.length !== 1 || !one || !/\.[0-9]{15}\.xml$/u.test(one)) {
      throw new Error(`Coverage role '${prefix}' must provide exactly one timestamped report.`);
    }
    reports.push(one);
  }
  const cli = everything.filter(
    (candidate) => basename(candidate) === "cli.coverage.cobertura.acceptance.xml",
  );
  if (cli.length !== 1 || cli[0] === undefined) {
    throw new Error("Published CLI coverage is missing or duplicated.");
  }
  checkCliCoverage(cli[0]);
  reports.push(cli[0]);
  for (const engine of browserEngines) {
    const [path] = everything.filter(
      (candidate) => basename(candidate) === `${engine}.coverage.cobertura.e2e.xml`,
    );
    if (path === undefined) {
      throw new Error(`Browser coverage role '${engine}' is missing.`);
    }
    checkBrowserCoverage(path);
    reports.push(path);
  }
  if (JSON.stringify([...reports].sort()) !== JSON.stringify(everything)) {
    throw new Error(
      "Coverage inputs contain a missing, duplicate, misplaced, or unexpected report.",
    );
  }
  return reports;
}
