// Reconcile ReportGenerator's merged class-line projection independently of its claimed rates.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { childrenNamed } from "../suites/xml.mjs";

/** @param {string[]} tiers @returns {string[]} Assembly identity comes from the component owner. */
function componentAssemblies(tiers) {
  /** @type {{ components: { tier: string, name: string }[] }} */
  const manifest = JSON.parse(
    readFileSync(
      fileURLToPath(new URL("../../../config/architecture.json", import.meta.url)),
      "utf8",
    ),
  );
  const names = manifest.components
    .filter((item) => tiers.includes(item.tier))
    .map((item) => item.name)
    .sort();
  if (names.length === 0 || new Set(names).size !== names.length) {
    throw new Error("Coverage requires a nonempty unique component set.");
  }
  return names;
}

/** @returns {string[]} Merged floors measure only registered product components. */
export function productionAssemblies() {
  return componentAssemblies(["product"]);
}

/** @typedef {{ lines: number, coveredLines: number, branches: number, coveredBranches: number }} Counts */
/** @returns {Counts} */
const empty = () => ({ lines: 0, coveredLines: 0, branches: 0, coveredBranches: 0 });

/** @param {string | undefined} value @returns {number} */
function integer(value) {
  const number = Number(value);
  if (value === undefined || !/^[0-9]+$/u.test(value) || !Number.isSafeInteger(number)) {
    throw new Error("Coverage measurements require nonnegative integer counts.");
  }
  return number;
}

/** @param {Counts} result @param {import("../suites/xml.mjs").XmlElement} line */
function countLine(result, line) {
  if (integer(line.attributes["number"]) < 1) {
    throw new Error("Coverage line number is invalid.");
  }
  result.lines += 1;
  result.coveredLines += integer(line.attributes["hits"]) > 0 ? 1 : 0;
  const branch = line.attributes["branch"]?.toLowerCase();
  if (branch === "false" || branch === undefined) {
    return;
  }
  const match = /^([0-9]+(?:\.[0-9]+)?)% \(([0-9]+)\/([0-9]+)\)$/u.exec(
    line.attributes["condition-coverage"] ?? "",
  );
  if (branch !== "true" || !match) {
    throw new Error("Coverage branch measurement is invalid.");
  }
  const covered = integer(match[2]);
  const valid = integer(match[3]);
  if (valid === 0 || covered > valid || Math.abs(Number(match[1]) - (100 * covered) / valid) > 1) {
    throw new Error("Coverage branch counts contradict their measurement.");
  }
  result.branches += valid;
  result.coveredBranches += covered;
}

/** @param {import("../suites/xml.mjs").XmlElement} pkg @returns {Counts} */
function countPackage(pkg) {
  const result = empty();
  const containers = childrenNamed(pkg, "classes");
  if (containers.length !== 1 || containers[0] === undefined) {
    throw new Error("Production packages require measured classes.");
  }
  const names = new Set();
  for (const klass of childrenNamed(containers[0], "class")) {
    const { name } = klass.attributes;
    if (!name || names.has(name)) {
      throw new Error("Coverage classes must have unique names.");
    }
    names.add(name);
    const sets = childrenNamed(klass, "lines");
    if (sets.length !== 1 || sets[0] === undefined) {
      throw new Error("Coverage classes require one line set.");
    }
    const numbers = new Set();
    for (const line of childrenNamed(sets[0], "line")) {
      const number = integer(line.attributes["number"]);
      if (numbers.has(number)) {
        throw new Error("Coverage class lines must be unique.");
      }
      numbers.add(number);
      countLine(result, line);
    }
  }
  if (result.lines === 0) {
    throw new Error("A production package cannot have empty measurement.");
  }
  return result;
}

/** @param {import("../suites/xml.mjs").XmlElement} root @param {string[]} expected @returns {{ total: Counts, packages: Map<string, Counts> }} */
export function reconcileMeasurements(root, expected) {
  const sets = childrenNamed(root, "packages");
  if (sets.length !== 1 || sets[0] === undefined) {
    throw new Error("Coverage requires one package set.");
  }
  const packages = childrenNamed(sets[0], "package");
  const names = packages.map((pkg) => pkg.attributes["name"] ?? "").sort();
  if (
    JSON.stringify(names) !== JSON.stringify([...expected].sort()) ||
    new Set(names).size !== names.length
  ) {
    throw new Error("Coverage production packages are missing, duplicated or unexpected.");
  }
  const total = empty();
  const measured = new Map();
  for (const pkg of packages) {
    const counts = countPackage(pkg);
    measured.set(pkg.attributes["name"], counts);
    for (const key of /** @type {(keyof Counts)[]} */ (Object.keys(total))) {
      total[key] += counts[key];
    }
  }
  for (const [covered, valid] of [
    ["lines-covered", "lines-valid"],
    ["branches-covered", "branches-valid"],
  ]) {
    const coveredCount = integer(root.attributes[covered ?? ""]);
    const validCount = integer(root.attributes[valid ?? ""]);
    if (validCount < 1 || coveredCount > validCount) {
      throw new Error("Coverage aggregate counters are invalid.");
    }
  }
  return { total, packages: measured };
}

/** Raw Coverlet instrumentation can include tooling exercised by production tests.
 * Preserve and reconcile every registered product/tooling package; merged floors filter to products.
 * @param {import("../suites/xml.mjs").XmlElement} root */
export function reconcileRawMeasurements(root) {
  const [packages] = childrenNamed(root, "packages");
  const names =
    packages === undefined
      ? []
      : childrenNamed(packages, "package").map((pkg) => pkg.attributes["name"] ?? "");
  const allowed = componentAssemblies(["product", "tooling"]);
  const products = productionAssemblies();
  if (
    !names.some((name) => products.includes(name)) ||
    names.some((name) => !allowed.includes(name))
  ) {
    throw new Error(
      "Raw coverage requires registered product/tooling measurements and a production package.",
    );
  }
  return reconcileMeasurements(root, names);
}
