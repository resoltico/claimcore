// Diagnostic ownership is registered by the creator independently of serialized run admission.
import assert from "node:assert/strict";
import { lstatSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { protectScratch, requirePrivateContext } from "./scratch-privacy.mjs";

/** @type {WeakMap<object,{directory:string,marker:string,identity:import("node:fs").Stats,directoryIdentity:import("node:fs").Stats,root:string,rootIdentity:import("node:fs").Stats,bytes:string}>} */
const destinations = new WeakMap();

/** @param {object} context @param {string} scratch @param {string} id */
export function registerRunDiagnostics(context, scratch, id) {
  const directory = join(scratch, "diagnostics");
  mkdirSync(directory, { mode: 0o700 });
  protectScratch(directory);
  const marker = join(directory, "context.json");
  const bytes = JSON.stringify({ format: 1, run: id });
  writeFileSync(marker, bytes, { mode: 0o600, flag: "wx" });
  requirePrivateContext(marker);
  destinations.set(context, {
    directory,
    marker,
    identity: lstatSync(marker),
    directoryIdentity: lstatSync(directory),
    root: scratch,
    rootIdentity: lstatSync(scratch),
    bytes,
  });
}

/** No field from a rejected context selects this destination.
 * @param {object} context @param {object} execution @param {"pending"|"accepted"|"refused"} admission */
export function retainRunDiagnostic(context, execution, admission) {
  try {
    const owned = destinations.get(context);
    assert.ok(owned);
    requirePrivateContext(owned.marker);
    /** @type {[string, import("node:fs").Stats][]} */
    const directories = [
      [owned.root, owned.rootIdentity],
      [owned.directory, owned.directoryIdentity],
    ];
    for (const [path, identity] of directories) {
      const current = lstatSync(path);
      assert.ok(current.isDirectory() && !current.isSymbolicLink());
      assert.equal(current.dev, identity.dev);
      assert.equal(current.ino, identity.ino);
    }
    const current = lstatSync(owned.marker);
    assert.equal(current.dev, owned.identity.dev);
    assert.equal(current.ino, owned.identity.ino);
    assert.equal(readFileSync(owned.marker, "utf8"), owned.bytes);
    const bytes = JSON.stringify({ format: 1, execution, admission });
    assert.ok(Buffer.byteLength(bytes) <= 16 * 1024);
    const path = join(owned.directory, `outcome-${admission}.json`);
    writeFileSync(path, `${bytes}\n`, { mode: 0o600, flag: "wx" });
    return path;
  } catch {
    const facts = boundedExecution(execution);
    process.stderr.write(
      `Execution ${facts}; diagnostic retention refused; evidence admission ${admission}; original execution/logs remain private.\n`,
    );
    return null;
  }
}

/** @param {object} execution */
function boundedExecution(execution) {
  const value = /** @type {{passed?:unknown,stages?:Record<string,unknown>}} */ (execution);
  const stages = Object.entries(value.stages ?? {})
    .filter(
      ([name, result]) =>
        /^[a-z0-9-]{1,80}$/u.test(name) &&
        typeof result === "string" &&
        /^exit [0-9]{1,3}(?:; captureFailed=(?:true|false))?$/u.test(result),
    )
    .slice(0, 10);
  return JSON.stringify({ passed: value.passed === true, stages: Object.fromEntries(stages) });
}
