import { readdirSync, readFileSync, lstatSync } from "node:fs";
import { join, relative, resolve } from "node:path";

export function workflowSources(root) {
  root = resolve(root);
  const result = new Map();
  function visit(directory) {
    for (const item of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, item.name);
      if (lstatSync(path).isSymbolicLink())
        throw new Error("Workflow sources may not use symlinks.");
      if (item.isDirectory()) visit(path);
      else if (item.isFile() && /\.ya?ml$/u.test(item.name)) {
        result.set(
          relative(root, path).split("\\").join("/"),
          readFileSync(path, "utf8"),
        );
      }
    }
  }
  visit(join(root, ".github"));
  return result;
}
