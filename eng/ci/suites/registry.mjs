// The registered test suites: what exists, how it is run and where its generated inventory lives.
import { readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * @typedef {object} Suite
 * @property {string} id
 * @property {"dotnet" | "vitest" | "playwright"} kind
 * @property {string} [assembly] .NET assembly name.
 * @property {string} [project] Repository-relative project file.
 * @property {"Release" | "Debug"} [configuration]
 * @property {boolean} [coverage] Whether the run collects Cobertura coverage.
 * @property {string[]} platforms Operating systems the suite runs on.
 * @property {string} [timeout] `dotnet test` timeout, such as `25m`.
 * @property {string[]} [build] Further projects the suite's processes need built.
 * @property {string[]} [msbuild] Extra build properties for the suite's own project.
 * @property {Record<string, string>} [env] Defaults with {root} and {results} path templates.
 * @property {string} [group] Suites of one group run together; `published` suites run elsewhere.
 * @property {{ selector: string, ids: string[] }} [partitions] Environment selector and its values.
 */

import { problems } from "./registry-validation.mjs";

/**
 * Where the suite's generated inventory is committed.
 * @param {Suite} suite
 * @returns {string}
 */
export const inventoryPath = (suite) => `tests/inventory/${suite.assembly ?? suite.id}.txt`;

/**
 * Read and validate config/test-suites.json.
 * @param {string} root Repository root.
 * @returns {Suite[]}
 */
export function loadSuites(root) {
  const registry = JSON.parse(readFileSync(join(root, "config/test-suites.json"), "utf8"));
  const { schemaVersion, suites: listed } = registry;
  if (schemaVersion !== 2 || !Array.isArray(listed)) {
    throw new Error("config/test-suites.json must be schema 2 with a suite list.");
  }
  /** @type {Suite[]} */
  const suites = listed;
  const ids = new Set();
  const projects = new Set();
  const inventories = new Set();
  const errors = suites.flatMap((suite) => {
    const invalid = problems(suite);
    if (!suite || typeof suite !== "object") {
      return invalid;
    }
    const duplicate = ids.has(suite.id) ? [`Suite ${suite.id} is registered twice.`] : [];
    if (suite.project && projects.has(suite.project)) {
      duplicate.push(`Suite ${suite.id} repeats a registered project.`);
    }
    if (inventories.has(inventoryPath(suite))) {
      duplicate.push(`Suite ${suite.id} repeats a registered inventory.`);
    }
    ids.add(suite.id);
    projects.add(suite.project);
    inventories.add(inventoryPath(suite));
    return [...duplicate, ...invalid];
  });
  if (errors.length > 0) {
    throw new Error(errors.join("\n"));
  }
  return suites;
}

/**
 * The built test assembly of a .NET suite.
 * @param {Suite} suite
 * @param {string} [configuration]
 * @returns {string}
 */
export function assemblyPath(suite, configuration = suite.configuration ?? "Release") {
  return `artifacts/bin/${suite.assembly}/${configuration.toLowerCase()}/${suite.assembly}.dll`;
}
