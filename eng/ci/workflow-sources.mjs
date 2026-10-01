import { readFileSync } from "node:fs";
import { join } from "node:path";
import { repositoryFiles } from "./repository.mjs";

/** @param {string} repository @returns {Map<string, string>} */
export function workflowSources(repository) {
  const files = repositoryFiles(repository).filter(
    (path) =>
      (path.startsWith(".github/") && /\.ya?ml$/u.test(path)) ||
      (path.startsWith("eng/ci/stage-plans/") && path.endsWith(".json")),
  );
  return new Map(files.map((path) => [path, readFileSync(join(repository, path), "utf8")]));
}
