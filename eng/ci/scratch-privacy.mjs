// Native permissions protect only a freshly-created orchestration scratch root.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { lstatSync, readFileSync, readdirSync, realpathSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { assertNoLinkAbove } from "./scan/files.mjs";
import { executable } from "./executable.mjs";

/** macOS ACL grants are independent of mode bits; reject every extended ACL.
 * Metadata is captured privately and never included in refusal diagnostics.
 * @param {string} path @param {boolean} directory */
function macAcl(path, directory) {
  const result = spawnSync("/bin/ls", ["-ldeq", "--", path], {
    env: { ...process.env, LC_ALL: "C" },
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    timeout: 30_000,
    maxBuffer: 16 * 1024,
  });
  assert.equal(result.status, 0, "Private orchestration metadata was refused.");
  const lines = result.stdout.trimEnd().split(/\r?\n/u);
  const permission = lines[0]?.split(/\s/u)[0];
  const expected = `${directory ? "drwx" : "-rw-"}------`;
  assert.ok(
    lines.length === 1 && (permission === expected || permission === `${expected}@`),
    "Private orchestration ACL was refused.",
  );
}

/** Linux group mode bits include the POSIX ACL mask; zero denies named non-owner grants.
 * @param {string} path @param {boolean} directory */
function posixPrivate(path, directory) {
  assert.ok(process.platform === "darwin" || process.platform === "linux");
  const stat = lstatSync(path);
  assert.equal(stat.uid, process.getuid?.(), "Private orchestration ownership was refused.");
  assert.equal(stat.mode & 0o7777, directory ? 0o700 : 0o600);
  assert.ok(directory ? stat.isDirectory() : stat.isFile());
  if (process.platform === "darwin") {
    macAcl(path, directory);
  }
}

/** @param {string} root @param {"protect"|"check"} mode */
function windowsAcl(root, mode) {
  assertNoLinkAbove(root);
  assert.equal(realpathSync(root), resolve(root));
  assert.ok(lstatSync(root).isDirectory());
  const result = spawnSync(
    executable("powershell"),
    [
      "-NoLogo",
      "-NoProfile",
      "-NonInteractive",
      "-Command",
      readFileSync(new URL("./scratch-privacy.ps1", import.meta.url), "utf8"),
    ],
    {
      env: { ...process.env, CLAIMCORE_SCRATCH_PATH: root, CLAIMCORE_SCRATCH_MODE: mode },
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      timeout: 30_000,
    },
  );
  assert.equal(result.status, 0, "Private orchestration scratch ACL was refused.");
}

/** The caller has just created this empty physical directory; no existing tree is modified.
 * @param {string} root */
export function protectScratch(root) {
  assertNoLinkAbove(root);
  assert.equal(realpathSync(root), resolve(root));
  assert.ok(lstatSync(root).isDirectory() && readdirSync(root).length === 0);
  if (process.platform === "win32") {
    windowsAcl(root, "protect");
  } else {
    posixPrivate(root, true);
  }
}

/** @param {string} contextPath */
export function requirePrivateContext(contextPath) {
  assertNoLinkAbove(contextPath);
  const stat = lstatSync(contextPath);
  assert.ok(stat.isFile() && stat.size < 16 * 1024);
  if (process.platform === "win32") {
    assert.equal(contextPath, join(dirname(contextPath), "context.json"));
    windowsAcl(dirname(contextPath), "check");
  } else {
    posixPrivate(dirname(contextPath), true);
    posixPrivate(contextPath, false);
  }
}
