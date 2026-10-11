// Only creator-inventoried report duplicates are disposable; whole run scratch remains unknown.
import assert from "node:assert/strict";
import { lstatSync, readdirSync, rmdirSync, unlinkSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { assertNoLinkAbove, fingerprint } from "./scan/files.mjs";

/** @typedef {{path:string, directory:boolean, identity:number[], unchanged:number[]}} Entry */
/** @param {string} root @param {string[]} files */
function expectedEntries(root, files) {
  assert.equal(root, resolve(root));
  const expected = new Map([[root, true]]);
  assert.equal(new Set(files).size, files.length);
  for (const name of files) {
    assert.ok(!isAbsolute(name));
    assert.ok(name.split("/").every((part) => part && part !== "." && part !== ".."));
    assert.ok(!name.includes("\\"));
    const path = resolve(root, name);
    assert.equal(relative(root, path).replaceAll("\\", "/"), name);
    for (let current = path; current !== root; current = dirname(current)) {
      const directory = current !== path;
      assert.ok(!expected.has(current) || expected.get(current) === directory);
      expected.set(current, directory);
    }
  }
  return expected;
}

/** @param {string} path @returns {Entry} */
function entry(path) {
  const stat = lstatSync(path);
  assert.ok(!stat.isSymbolicLink() && (stat.isDirectory() || stat.isFile()));
  assert.ok(stat.isDirectory() || stat.nlink === 1);
  return {
    path,
    directory: stat.isDirectory(),
    identity: [stat.dev, stat.ino, stat.mode, stat.uid, stat.gid],
    unchanged: [stat.nlink, stat.size, stat.mtimeMs, stat.ctimeMs],
  };
}

/** Capture exact known entries without adopting extra files or empty directories.
 * @param {string} root @param {string[] | undefined} files @returns {Entry[] | null} */
export function evidenceStageOwnership(root, files) {
  try {
    assert.ok(files);
    assertNoLinkAbove(root);
    const expected = expectedEntries(root, files);
    /** @type {Entry[]} */
    const entries = [];
    const pending = [root];
    for (let path = pending.pop(); path !== undefined; path = pending.pop()) {
      assert.ok(expected.has(path), "Unknown stage entries are preserved.");
      const observed = entry(path);
      assert.equal(observed.directory, expected.get(path));
      expected.delete(path);
      entries.push(observed);
      if (observed.directory) {
        pending.push(...readdirSync(path).map((name) => join(path, name)));
      }
    }
    assert.equal(expected.size, 0);
    return entries;
  } catch {
    return null;
  }
}

/** @param {Entry[]} owned @param {boolean} unchanged */
function requireOwned(owned, unchanged) {
  for (const before of owned) {
    assertNoLinkAbove(before.path);
    const after = entry(before.path);
    assert.equal(after.directory, before.directory);
    assert.deepEqual(after.identity, before.identity);
    if (unchanged) {
      assert.deepEqual(after.unchanged, before.unchanged);
    }
  }
}

/** Best-effort exact-entry removal. Mid-cleanup refusal may leave a partial duplicate tree.
 * @param {string} staging @param {string} retained @param {string} qualified
 * @param {Entry[] | null} staged @param {Entry[] | null} delivered @returns {boolean} */
export function removeEvidenceStage(staging, retained, qualified, staged, delivered) {
  try {
    assert.ok(staged && delivered);
    const files = staged.filter((item) => !item.directory);
    const names = files.map((item) => relative(staging, item.path).replaceAll("\\", "/"));
    const current = evidenceStageOwnership(staging, names);
    const copy = evidenceStageOwnership(retained, names);
    assert.ok(current && copy);
    requireOwned(staged, true);
    requireOwned(delivered, true);
    assert.equal(fingerprint(staging), qualified);
    assert.equal(fingerprint(retained), qualified);
    for (const file of files) {
      requireOwned(staged.filter((item) => item.directory).concat(file), false);
      assert.deepEqual(entry(file.path).unchanged, file.unchanged);
      unlinkSync(file.path);
    }
    const directories = staged.filter((item) => item.directory);
    directories.sort((left, right) => right.path.length - left.path.length);
    for (const directory of directories) {
      requireOwned([directory], false);
      rmdirSync(directory.path);
    }
    return true;
  } catch {
    return false;
  }
}
