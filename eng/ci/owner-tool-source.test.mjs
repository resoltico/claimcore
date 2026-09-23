import test from "node:test";
import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  writeFileSync,
  rmSync,
  symlinkSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { toolProvenance } from "./owner-tool-source.mjs";
function repository() {
  const root = mkdtempSync(join(tmpdir(), "claimcore-review-source-"));
  const git = (...args) =>
    execFileSync("git", ["-C", root, ...args], { stdio: "pipe" });
  git("init", "--quiet");
  git("config", "user.email", "synthetic@invalid.local");
  git("config", "user.name", "Synthetic review");
  mkdirSync(join(root, "eng/ci"), { recursive: true });
  writeFileSync(join(root, "eng/ci/policy.mjs"), "export const policy = 1;\n");
  git("add", "--", "eng/ci/policy.mjs");
  git("commit", "--quiet", "-m", "fixture");
  return {
    root,
    git,
    close: () => rmSync(root, { recursive: true, force: true }),
  };
}
test("provenance binds exact committed reporting bytes", () => {
  const f = repository();
  try {
    const result = toolProvenance(f.root);
    assert.equal(result.commit, f.git("rev-parse", "HEAD").toString().trim());
    assert.equal(result.files.length, 1);
    assert.match(result.sha256, /^[a-f0-9]{64}$/u);
  } finally {
    f.close();
  }
});
test("assume-unchanged cannot hide reporting-tool changes", () => {
  const f = repository();
  try {
    f.git("update-index", "--assume-unchanged", "eng/ci/policy.mjs");
    writeFileSync(
      join(f.root, "eng/ci/policy.mjs"),
      "export const policy = 0;\n",
    );
    assert.equal(f.git("status", "--porcelain").toString(), "");
    assert.throws(() => toolProvenance(f.root), /bytes differ/u);
  } finally {
    f.close();
  }
});
test("untracked helpers cannot impersonate a reviewed tool revision", () => {
  const f = repository();
  try {
    writeFileSync(join(f.root, "eng/ci/extra.mjs"), "export const x = 0;");
    assert.throws(() => toolProvenance(f.root), /Untracked/u);
  } finally {
    f.close();
  }
});
test("symlinked reporting directories are refused before reading their files", () => {
  const f = repository();
  try {
    mkdirSync(join(f.root, "other"));
    rmSync(join(f.root, "eng/ci"), { recursive: true });
    symlinkSync(join(f.root, "other"), join(f.root, "eng/ci"), "junction");
    assert.throws(() => toolProvenance(f.root), /symlinks/u);
  } finally {
    f.close();
  }
});
