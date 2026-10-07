import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import test from "node:test";
import { producingInputFiles } from "../../eng/ci/publish/inputs.mjs";
import { recordsFor, treeHash, sourceFiles } from "./asset-manifest.mjs";

test("asset inventory excludes only the root manifest and orders paths ordinally", async () => {
  const root = await mkdtemp(join(tmpdir(), "claimcore-asset-records-"));
  try {
    await mkdir(join(root, "nested"));
    for (const name of [
      "claimcore-assets.manifest.json",
      "nested/claimcore-assets.manifest.json",
      "B.txt",
      "a.txt",
    ]) {
      await writeFile(join(root, name), "synthetic");
    }
    const records = await recordsFor(root, "claimcore-assets.manifest.json");
    assert.deepEqual(
      records.map(({ path }) => path),
      ["B.txt", "a.txt", "nested/claimcore-assets.manifest.json"],
    );
    await writeFile(join(root, "nested/claimcore-assets.manifest.json"), "changed");
    assert.notEqual(
      treeHash(records),
      treeHash(await recordsFor(root, "claimcore-assets.manifest.json")),
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("source identity distinguishes newline-containing paths from multiple records", () => {
  const digest = createHash("sha256").update("same synthetic contents").digest("hex");
  const separate = [
    { path: "a", sha256: digest },
    { path: "b", sha256: digest },
  ];
  const joined = [{ path: `a\n${digest}\nb`, sha256: digest }];
  assert.notEqual(treeHash(separate), treeHash(joined));
  assert.equal(
    treeHash([
      { path: "a", sha256: "0".repeat(64) },
      { path: "b", sha256: "0".repeat(64) },
    ]),
    "fa5a66f488295200dc3ba10e2d7d85d205c20b8df2c96587488ff9bf3d100516",
  );
});

const root = resolve(import.meta.dirname, "../..");
test("publication inputs include every frontend asset producer input and embedded database input", async () => {
  const included = new Set(producingInputFiles(root));
  for (const path of await sourceFiles()) {
    assert.ok(
      included.has(relative(root, path).split("\\").join("/")),
      "Asset input missing from producing identity.",
    );
  }
  const baseline = /** @type {{fragments: string[]}} */ (
    JSON.parse(await readFile(join(root, "db/schema-baseline.json"), "utf8"))
  );
  baseline.fragments.forEach((name) => assert.ok(included.has(`db/baseline/${name}`)));
  for (const path of [
    "db/schema-baseline.json",
    "db/postgresql-baseline.json",
    "global.json",
    "config/contracts.lock.json",
  ]) {
    assert.ok(included.has(path));
  }
});
