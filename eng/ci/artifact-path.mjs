// Generated outputs may live only under this checkout's artifacts tree, without link traversal.
import { lstatSync } from "node:fs";
import { isAbsolute, join, relative, resolve, sep } from "node:path";

/** @param {string} root @param {string} value @returns {string} */
export function artifactDirectory(root, value) {
  const repository = resolve(root);
  const output = resolve(repository, value);
  const path = relative(repository, output);
  if (isAbsolute(path) || !path.startsWith(`artifacts${sep}`)) {
    throw new Error(
      `Result path ${JSON.stringify(output)} must be below ${JSON.stringify(join(repository, "artifacts"))}. Use --results-root artifacts/results-<unique-run-id>.`,
    );
  }
  let current = repository;
  for (const part of path.split(sep)) {
    current = join(current, part);
    const stat = lstatSync(current, { throwIfNoEntry: false });
    if (stat && (stat.isSymbolicLink() || !stat.isDirectory())) {
      throw new Error("Generated result directories may not traverse links or regular files.");
    }
  }
  return output;
}
