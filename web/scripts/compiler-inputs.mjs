// Bind native compiler inheritance to retained asset identity.
import { readFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { parseJsonc } from "../../eng/lint/jsonc.mjs";
import { resolveSourceFile } from "../../eng/ci/repository-path.mjs";

/** @param {string} root @param {string[]} inputs @returns {string[]} */
export function compilerInputs(root, inputs) {
  const seen = new Set();
  const visiting = new Set();
  /** @param {string} file */
  function visit(file) {
    if (visiting.has(file)) {
      throw new Error("Compiler configuration inheritance contains a cycle.");
    }
    if (seen.has(file)) {
      return;
    }
    const path = relative(root, file).split("\\").join("/");
    resolveSourceFile(root, path);
    visiting.add(file);
    const config = /** @type {{extends?: string | string[]}} */ (
      parseJsonc(readFileSync(file, "utf8"))
    );
    const parents = typeof config.extends === "string" ? [config.extends] : (config.extends ?? []);
    if (!Array.isArray(parents) || !parents.every((parent) => typeof parent === "string")) {
      throw new Error("Compiler inheritance requires repository-local paths.");
    }
    for (const parent of parents) {
      visit(resolve(dirname(file), parent));
    }
    visiting.delete(file);
    seen.add(file);
  }
  inputs.forEach(visit);
  return [...seen].sort();
}
