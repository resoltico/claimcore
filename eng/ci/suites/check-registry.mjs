// Holds the suite registry to the repository: every test project is registered, every registered
// file exists, every inventory belongs to a suite, and nothing repeats a test count that the
// inventories already own.
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { inventoryPath, loadSuites } from "./registry.mjs";

/**
 * @param {string} root
 * @param {string} directory Repository-relative.
 * @param {RegExp} pattern
 * @returns {string[]} Repository-relative files below `directory` whose path matches `pattern`.
 */
function filesMatching(root, directory, pattern) {
  const absolute = join(root, directory);
  if (!existsSync(absolute)) {
    return [];
  }
  return readdirSync(absolute, { withFileTypes: true }).flatMap((entry) => {
    const path = `${directory}/${entry.name}`;
    if (entry.isDirectory()) {
      return entry.name === "node_modules" || entry.name === "bin" || entry.name === "obj"
        ? []
        : filesMatching(root, path, pattern);
    }
    return pattern.test(path) ? [path] : [];
  });
}

/**
 * @param {string} root Repository root.
 * @returns {string[]} Problems found; empty when the registry matches the repository.
 */
export function checkRegistry(root) {
  const suites = loadSuites(root);
  /** @type {string[]} */
  const errors = [];
  const registeredProjects = new Set(
    suites.flatMap((suite) => (suite.project ? [suite.project] : [])),
  );
  for (const project of filesMatching(root, "tests", /^tests\/[^/]+Tests\/[^/]+\.fsproj$/u)) {
    if (!registeredProjects.has(project)) {
      errors.push(`${project} is a test project that no suite registers.`);
    }
  }
  for (const suite of suites) {
    for (const path of [suite.project, ...(suite.build ?? []), inventoryPath(suite)]) {
      if (path !== undefined && !existsSync(join(root, path))) {
        errors.push(`Suite ${suite.id} names ${path}, which does not exist.`);
      }
    }
  }
  const inventories = new Set(suites.map(inventoryPath));
  for (const file of filesMatching(root, "tests/inventory", /\.txt$/u)) {
    if (!inventories.has(file)) {
      errors.push(`${file} belongs to no registered suite.`);
    }
  }
  const sources = [
    ...filesMatching(root, ".github/workflows", /\.ya?ml$/u),
    ...filesMatching(root, "eng", /\.(sh|mjs)$/u).filter((path) => !path.endsWith(".test.mjs")),
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
