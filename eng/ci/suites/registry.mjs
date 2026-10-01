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
 * @property {string} [group] Suites of one group run together; `published` suites run elsewhere.
 * @property {{ selector: string, ids: string[] }} [partitions] Environment selector and its values.
 */

const kinds = new Set(["dotnet", "vitest", "playwright"]);
const platforms = new Set(["linux", "macos", "windows"]);
const identifier = /^[a-z][a-z0-9-]*$/u;

/**
 * @param {Suite} suite
 * @returns {string[]}
 */
function problems(suite) {
  /** @type {string[]} */
  const found = [];
  if (!identifier.test(suite.id)) {
    found.push(`Suite id '${suite.id}' must be lowercase kebab-case.`);
  }
  if (!kinds.has(suite.kind)) {
    found.push(`Suite ${suite.id} has an unknown kind.`);
  }
  if (suite.kind === "dotnet") {
    for (const key of /** @type {const} */ (["assembly", "project", "configuration", "timeout"])) {
      if (typeof suite[key] !== "string") {
        found.push(`Suite ${suite.id} needs ${key}.`);
      }
    }
  }
  if (!Array.isArray(suite.platforms) || !suite.platforms.every((os) => platforms.has(os))) {
    found.push(`Suite ${suite.id} lists unknown platforms.`);
  }
  return found;
}

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
  const errors = suites.flatMap((suite) => {
    const duplicate = ids.has(suite.id) ? [`Suite ${suite.id} is registered twice.`] : [];
    ids.add(suite.id);
    return [...duplicate, ...problems(suite)];
  });
  if (errors.length > 0) {
    throw new Error(errors.join("\n"));
  }
  return suites;
}

/**
 * Where the suite's generated inventory is committed.
 * @param {Suite} suite
 * @returns {string}
 */
export const inventoryPath = (suite) => `tests/inventory/${suite.assembly ?? suite.id}.txt`;

/**
 * The built test assembly of a .NET suite.
 * @param {Suite} suite
 * @param {string} [configuration]
 * @returns {string}
 */
export function assemblyPath(suite, configuration = suite.configuration ?? "Release") {
  return `artifacts/bin/${suite.assembly}/${configuration.toLowerCase()}/${suite.assembly}.dll`;
}
