// Cleanup belongs to the settled creator, never a later age/PID-based sweep.
import { runContext } from "./run-context.mjs";
import assert from "node:assert/strict";
import { lstatSync, readdirSync, realpathSync, rmSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { assertNoLinkAbove, regularFiles } from "./scan/files.mjs";

/** @param {string} root @param {string} prefix */
function entries(root, prefix) {
  const paths = new Set([prefix]);
  for (const file of regularFiles(root)) {
    let path = `${prefix}/${relative(root, file).replaceAll("\\", "/")}`;
    paths.add(path);
    while (path.includes("/")) {
      path = path.slice(0, path.lastIndexOf("/"));
      paths.add(path);
    }
  }
  return paths;
}
/** A no-job run may remove only its exact captured source and known empty orchestration directories.
 * @param {import("./run-context.mjs").RunContext} context */
export function untouchedSnapshot(context) {
  const expected = entries(join(dirname(context.results), "inputs"), "source");
  expected.add("context.json");
  expected.add("source/artifacts");
  expected.add("source/artifacts/local-ci");
  const pending = [context.scratch];
  for (let path = pending.pop(); path !== undefined; path = pending.pop()) {
    const name = relative(context.scratch, path).replaceAll("\\", "/");
    if (name !== "" && !expected.has(name)) {
      return false;
    }
    const stat = lstatSync(path);
    if (stat.isSymbolicLink()) {
      return false;
    }
    if (stat.isDirectory()) {
      pending.push(...readdirSync(path).map((child) => join(path, child)));
    } else if (!stat.isFile()) {
      return false;
    }
  }
  return true;
}
/** @param {import("./run-context.mjs").RunContext} context */
export function removeSettledScratch(context) {
  const { scratch } = context;
  assert.ok(untouchedSnapshot(context), "Unknown run entries cannot be cleaned.");
  runContext(context.source, join(scratch, "context.json"));
  assertNoLinkAbove(scratch);
  assert.equal(realpathSync(scratch), scratch);
  // Node removes symbolic entries themselves; it never traverses their targets.
  rmSync(scratch, { recursive: true });
}
