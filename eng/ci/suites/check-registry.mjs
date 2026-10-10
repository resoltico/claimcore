import { projectMembershipProblems } from "./project-membership.mjs";
// Holds the suite registry to the repository: every test project is registered, every registered
// file exists, every inventory belongs to a suite, and nothing repeats a test count that the
// inventories already own.
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryFiles } from "../repository.mjs";
import { inventoryPath, loadSuites } from "./registry.mjs";

/** @param {string} root @param {import("./registry.mjs").Suite[]} suites @returns {string[]} */
function suiteProblems(root, suites) {
  const errors = [];
  for (const suite of suites) {
    if (
      suite.kind === "dotnet" &&
      suite.group !== undefined &&
      !["postgres", "published"].includes(suite.group)
    ) {
      errors.push(`Suite ${suite.id} belongs to no required CI execution family.`);
    }
    if (suite.group === "published" && suite.id !== "acceptance") {
      errors.push(`Suite ${suite.id} has no published execution entry point.`);
    }
    for (const path of [suite.project, ...(suite.build ?? []), inventoryPath(suite)]) {
      if (path !== undefined && !existsSync(join(root, path))) {
        errors.push(`Suite ${suite.id} names ${path}, which does not exist.`);
      }
    }
  }
  return errors;
}

/**
 * @param {string} root Repository root.
 * @returns {string[]} Problems found; empty when the registry matches the repository.
 */
export function checkRegistry(root) {
  const suites = loadSuites(root);
  const files = repositoryFiles(root);
  const filesMatching = (/** @type {string} */ directory, /** @type {RegExp} */ pattern) =>
    files.filter((path) => path.startsWith(`${directory}/`) && pattern.test(path));
  /** @type {string[]} */
  const errors = [];
  errors.push(
    ...projectMembershipProblems(
      root,
      files.filter((path) => path.endsWith(".fsproj")),
      suites,
    ),
  );
  errors.push(...suiteProblems(root, suites));
  const inventories = new Set(suites.map(inventoryPath));
  for (const file of filesMatching("tests/inventory", /\.txt$/u)) {
    if (!inventories.has(file)) {
      errors.push(`${file} belongs to no registered suite.`);
    }
  }
  const sources = [
    ...filesMatching(".github/workflows", /\.ya?ml$/u),
    ...filesMatching("eng", /\.(sh|mjs)$/u).filter((path) => !path.endsWith(".test.mjs")),
  ];
  for (const path of sources) {
    if (/--minimum-expected-tests=\d+/u.test(readFileSync(join(root, path), "utf8"))) {
      errors.push(`${path} repeats a test count; the inventories own every count.`);
    }
  }
  return errors;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
  const errors = checkRegistry(root);
  for (const error of errors) {
    process.stderr.write(`${error}\n`);
  }
  if (errors.length === 0) {
    process.stdout.write("The suite registry matches the repository.\n");
  }
  process.exitCode = errors.length === 0 ? 0 : 1;
}
