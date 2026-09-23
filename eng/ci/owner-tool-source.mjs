import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readdirSync, lstatSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { digest, isSha } from "./owner-review-scope.mjs";

function diskFiles(root, relative) {
  const path = join(root, relative);
  const stat = lstatSync(path);
  assert(!stat.isSymbolicLink(), "Reporting tools must not be symlinks.");
  if (stat.isFile()) return [relative];
  assert(stat.isDirectory(), "Unexpected reporting-tool filesystem entry.");
  return readdirSync(path).flatMap((entry) =>
    diskFiles(root, `${relative}/${entry}`),
  );
}

export function toolProvenance(root) {
  const git = (...args) =>
    execFileSync("git", ["-C", root, ...args], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
  const commit = git("rev-parse", "HEAD").trim();
  assert(isSha(commit), "Reporting tools require a committed source revision.");
  assert(
    !lstatSync(join(root, "eng")).isSymbolicLink(),
    "Reporting-tool directory must not be a symlink.",
  );
  const actualFiles = diskFiles(root, "eng/ci").sort();
  const entries = git("ls-tree", "-rz", "HEAD", "--", "eng/ci")
    .split("\0")
    .filter(Boolean)
    .map((row) => {
      const match =
        /^(100644|100755) blob ([0-9a-f]{40})\t(eng\/ci\/.+)$/u.exec(row);
      assert(match, "Reporting tools must be committed regular files.");
      const [, mode, sha, path] = match;
      const stat = lstatSync(join(root, path));
      assert(
        stat.isFile() && !stat.isSymbolicLink(),
        "Reporting tool is not a regular file.",
      );
      const bytes = readFileSync(join(root, path));
      const actual = createHash("sha1")
        .update(`blob ${bytes.length}\0`)
        .update(bytes)
        .digest("hex");
      assert.equal(
        actual,
        sha,
        "Reporting-tool bytes differ from the committed revision.",
      );
      if (process.platform !== "win32")
        assert.equal(
          stat.mode & 0o111 ? "100755" : "100644",
          mode,
          "Reporting-tool mode differs.",
        );
      return { path, mode, sha };
    })
    .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  assert(entries.length > 0, "Reporting tools are missing.");
  assert.deepEqual(
    actualFiles,
    entries.map((entry) => entry.path),
    "Untracked reporting-tool files.",
  );
  assert.equal(
    git("rev-parse", "HEAD").trim(),
    commit,
    "Reporting-tool revision changed.",
  );
  return { commit, sha256: digest(entries), files: entries };
}
