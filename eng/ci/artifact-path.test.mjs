import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { artifactDirectory, cleanDirectories } from "./artifact-path.mjs";

test("results and cleanup cannot escape the generated tree or target its root", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-artifact-scope-"));
  try {
    assert.equal(
      artifactDirectory(root, "artifacts/results/unit"),
      join(root, "artifacts/results/unit"),
    );
    assert.deepEqual(cleanDirectories(root, ["artifacts/test-results"]), [
      join(root, "artifacts/test-results"),
    ]);
    for (const path of [".local", "src", "../outside", "artifacts", "artifacts/../../outside"]) {
      assert.throws(
        () => artifactDirectory(root, path),
        (error) => {
          assert(error instanceof Error);
          assert(error.message.includes(join(root, "artifacts")));
          assert.match(error.message, /--results-root artifacts\/results/u);
          return true;
        },
      );
      assert.throws(() => cleanDirectories(root, [path]));
    }
    mkdirSync(join(root, "private"));
    symlinkSync(join(root, "private"), join(root, "artifacts"), "junction");
    assert.throws(() => artifactDirectory(root, "artifacts/results"), /links/u);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
