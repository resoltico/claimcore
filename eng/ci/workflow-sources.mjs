import { readdirSync, readFileSync, lstatSync } from "node:fs";
import { join, relative, resolve } from "node:path";

/**
 * Workflow, composite-action and stage-plan sources by repository-relative path.
 * @param {string} repository
 * @returns {Map<string, string>}
 */
export function workflowSources(repository) {
  const root = resolve(repository);
  /** @type {Map<string, string>} */
  const result = new Map();
  /** @param {string} directory */
  function visit(directory) {
    for (const item of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, item.name);
      if (lstatSync(path).isSymbolicLink())
        throw new Error("Workflow sources may not use symlinks.");
      if (item.isDirectory()) visit(path);
      else if (item.isFile() && /\.ya?ml$/u.test(item.name)) {
        result.set(relative(root, path).split("\\").join("/"), readFileSync(path, "utf8"));
      }
    }
  }
  visit(join(root, ".github"));
  // The stage plans are part of what a workflow runs, so governance reads them with the workflows.
  const plans = join(root, "eng/ci/stage-plans");
  for (const item of readdirSync(plans, { withFileTypes: true })) {
    const path = join(plans, item.name);
    if (lstatSync(path).isSymbolicLink()) throw new Error("Stage plans may not use symlinks.");
    if (item.isFile() && /\.json$/u.test(item.name))
      result.set(`eng/ci/stage-plans/${item.name}`, readFileSync(path, "utf8"));
  }
  return result;
}
