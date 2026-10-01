import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { createManifest, treeDigest, verifyTree, writeManifest } from "./tree.mjs";

/**
 * @param {Record<string, string>} files
 * @param {(root: string, manifest: string) => void} body
 */
function withTree(files, body) {
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-publish-"));
  try {
    const root = join(scratch, "tree");
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    body(root, join(scratch, "manifest.json"));
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

const files = {
  "app.dll": "binary",
  "wwwroot/index.html": "<html>",
  "wwwroot/B.txt": "b",
  "wwwroot/a.txt": "a",
};

test("a written manifest verifies its tree and lists files in ordinal order", () => {
  withTree(files, (root, manifest) => {
    writeManifest("cli", root, manifest);
    assert.match(verifyTree("cli", root, manifest), /^[0-9a-f]{64}$/u);
    const paths = createManifest("cli", root).files.map((file) => file.path);
    assert.deepEqual(paths, ["app.dll", "wwwroot/B.txt", "wwwroot/a.txt", "wwwroot/index.html"]);
  });
});

test("any change to the tree is refused", () => {
  const mutations = [
    (/** @type {string} */ root) => writeFileSync(join(root, "app.dll"), "tampered"),
    (/** @type {string} */ root) => writeFileSync(join(root, "extra.txt"), "x"),
    (/** @type {string} */ root) => rmSync(join(root, "wwwroot/a.txt")),
    (/** @type {string} */ root) => symlinkSync(join(root, "app.dll"), join(root, "link.dll")),
  ];
  for (const mutate of mutations) {
    withTree(files, (root, manifest) => {
      writeManifest("cli", root, manifest);
      mutate(root);
      assert.throws(() => verifyTree("cli", root, manifest), /./u);
    });
  }
});

test("a manifest for another product or with a forged shape is refused", () => {
  withTree(files, (root, manifest) => {
    writeManifest("cli", root, manifest);
    assert.throws(() => verifyTree("web", root, manifest), /different product/u);
    const parsed = JSON.parse(readFileSync(manifest, "utf8"));
    /** @param {object} value */
    const rewrite = (value) => writeFileSync(manifest, JSON.stringify(value));
    rewrite({ ...parsed, extra: 1 });
    assert.throws(() => verifyTree("cli", root, manifest), /unknown properties/u);
    rewrite({ ...parsed, treeSha256: "0".repeat(64) });
    assert.throws(() => verifyTree("cli", root, manifest), /digest/u);
    rewrite({
      ...parsed,
      files: [...parsed.files].reverse(),
      treeSha256: treeDigest([...parsed.files].reverse()),
    });
    assert.throws(() => verifyTree("cli", root, manifest), /sorted/u);
    const unsafe = [{ ...parsed.files[0], path: "../escape" }];
    rewrite({ ...parsed, files: unsafe, treeSha256: treeDigest(unsafe) });
    assert.throws(() => verifyTree("cli", root, manifest), /invalid file record/u);
  });
});

test("an empty tree cannot be published", () => {
  withTree({}, () => undefined);
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-publish-empty-"));
  try {
    assert.throws(() => createManifest("cli", scratch), /empty/u);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
