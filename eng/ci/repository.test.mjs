import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { executable } from "./executable.mjs";
import { repositoryFiles } from "./repository.mjs";
import { copySource, sourceFingerprint } from "./source-snapshot.mjs";

/** @param {(root: string) => void} run */
function fixture(run) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-source-test-"));
  try {
    run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

/** @param {string} root @param {string} path @param {string} text */
function write(root, path, text) {
  mkdirSync(dirname(join(root, path)), { recursive: true });
  writeFileSync(join(root, path), text);
}

/** @param {string} root @param {string[]} args */
function git(root, args) {
  assert.equal(spawnSync(executable("git"), args, { cwd: root, stdio: "ignore" }).status, 0);
}

test("one inventory includes hidden, force-tracked and newline paths but excludes ignored and deleted source", () =>
  fixture((root) => {
    git(root, ["init", "--quiet"]);
    write(root, ".gitignore", ".local/\nartifacts/\nignored.txt\n");
    write(root, "ignored.txt", "synthetic tracked input");
    write(root, "removed.txt", "remove");
    git(root, ["add", "--force", "ignored.txt", "removed.txt"]);
    rmSync(join(root, "removed.txt"));
    write(root, ".github/workflows/check.yml", "name: check\n");
    write(root, "line\nbreak.txt", "newline name");
    write(root, ".local/private.txt", "private synthetic input");
    assert.deepEqual(repositoryFiles(root), [
      ".github/workflows/check.yml",
      ".gitignore",
      "ignored.txt",
      "line\nbreak.txt",
    ]);
  }));

test("plain trees use repository ignores and an invalid Git marker fails closed", () =>
  fixture((root) => {
    write(root, ".gitignore", "artifacts/\n");
    write(root, "source.txt", "source");
    write(root, "artifacts/output.txt", "output");
    assert.deepEqual(repositoryFiles(root), [".gitignore", "source.txt"]);
    write(root, ".git", "invalid worktree marker\n");
    assert.throws(() => repositoryFiles(root), /invalid or inaccessible/u);
  }));

test("a clean snapshot includes current edits without private or build state", () =>
  fixture((root) =>
    fixture((destination) => {
      write(root, ".gitignore", "artifacts/\n.local/\n");
      write(root, "source.txt", "current edited source");
      write(root, ".local/input.txt", "private synthetic input");
      write(root, "artifacts/output.txt", "stale output");
      const fingerprint = copySource(root, destination);
      assert.equal(sourceFingerprint(destination), fingerprint);
      assert.deepEqual(repositoryFiles(destination), [".gitignore", "source.txt"]);
      write(root, "source.txt", "another edit");
      assert.notEqual(sourceFingerprint(root), fingerprint);
    }),
  ));

test("source edits during copying invalidate the snapshot", () =>
  fixture((root) =>
    fixture((destination) => {
      write(root, "input.txt", "initial source");
      assert.throws(
        () =>
          copySource(root, destination, (source, output) => {
            copyFileSync(source, output);
            write(root, "input.txt", "changed during copy");
          }),
        /Source changed/u,
      );
    }),
  ));
