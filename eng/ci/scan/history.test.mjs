import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { installTool } from "../tools.mjs";
import { observeHistory, scanHistory } from "./history.mjs";
import { gitEnvironment } from "./process.mjs";

const root = resolve(import.meta.dirname, "../../..");
const binary = await installTool(root, "gitleaks");
const canary = `aws_access_key_id = ${"AK"}${"IA7JQ4N2P6R8T0V3X5"}\naws_secret_access_key = ${"7Yk3pQ9v"}${"L2mN8cR4"}${"tW6xZ1aB"}${"5dF0hJ7s"}${"K9uE3iO6"}\n`;
/** @param {string} dir @param {string[]} args */
function git(dir, args) {
  return execFileSync("git", args, {
    cwd: dir,
    encoding: "utf8",
    env: gitEnvironment(),
    stdio: "pipe",
  }).trim();
}
/** @param {(dir:string)=>Promise<void>} body */
async function fixture(body) {
  const dir = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-history-test-"));
  try {
    git(dir, ["init", "-b", "main"]);
    git(dir, ["config", "commit.gpgsign", "false"]);
    git(dir, ["config", "core.hooksPath", join(dir, ".git/owned-empty-hooks")]);
    git(dir, ["config", "user.name", "Synthetic"]);
    git(dir, ["config", "user.email", "synthetic@example.invalid"]);
    writeFileSync(join(dir, "file.txt"), "safe\n");
    commit(dir, "safe");
    await body(dir);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
/** @param {string} dir @param {string} message */
function commit(dir, message) {
  git(dir, ["add", "-A"]);
  git(dir, ["commit", "-m", message]);
}
/** @param {string} dir @param {string} [gitleaks] */
async function scan(dir, gitleaks = binary) {
  let output = "";
  const capture = (/** @type {string} */ text) => {
    output += text;
  };
  const status = await scanHistory({ root: dir, gitleaks, stdout: capture, stderr: capture });
  assert.ok(!output.includes(canary.trim()) && !output.includes(dir));
  return status;
}

test("complete history detects deleted, renamed, binary-attributed and replacement-masked content", async () => {
  await fixture(async (dir) => {
    assert.equal(await scan(dir), 0);
    const safe = git(dir, ["rev-parse", "HEAD"]);
    writeFileSync(join(dir, ".gitattributes"), "*.txt -diff\n");
    writeFileSync(join(dir, "file.txt"), canary);
    commit(dir, "synthetic detection control");
    const secret = git(dir, ["rev-parse", "HEAD"]);
    git(dir, ["mv", "file.txt", "renamed.txt"]);
    commit(dir, "rename");
    writeFileSync(join(dir, "renamed.txt"), "safe\n");
    commit(dir, "remove");
    git(dir, ["branch", "extra", secret]);
    git(dir, ["replace", secret, safe]);
    assert.equal(await scan(dir), 1);
  });
});

test("separate merge diffs detect a merge-only addition followed by deletion", async () => {
  await fixture(async (dir) => {
    git(dir, ["checkout", "-b", "side"]);
    writeFileSync(join(dir, "side.txt"), "side\n");
    commit(dir, "side");
    git(dir, ["checkout", "main"]);
    writeFileSync(join(dir, "main.txt"), "main\n");
    commit(dir, "main");
    git(dir, ["merge", "--no-commit", "--no-ff", "side"]);
    writeFileSync(join(dir, "merge.txt"), canary);
    commit(dir, "merge resolution");
    rmSync(join(dir, "merge.txt"));
    commit(dir, "delete merge addition");
    assert.equal(await scan(dir), 1);
  });
});

test("grafts, shallow, absent and partial history and execution failure refuse safely", async () => {
  await fixture(async (dir) => {
    assert.equal(await scan(dir, join(dir, "absent-scanner")), 2);
    const { common } = await observeHistory(dir);
    writeFileSync(join(common, "info/grafts"), "");
    assert.equal(await scan(dir), 2);
    rmSync(join(common, "info/grafts"));
    writeFileSync(join(common, "shallow"), git(dir, ["rev-parse", "HEAD"]));
    assert.equal(await scan(dir), 2);
    rmSync(join(common, "shallow"));
    git(dir, ["config", "extensions.partialclone", "origin"]);
    assert.equal(await scan(dir), 2);
    rmSync(join(dir, ".git"), { recursive: true });
    assert.equal(await scan(dir), 2);
  });
});

test("history disables external diff and text conversion and scrubs redirection/override inputs", async () => {
  await fixture(async (dir) => {
    writeFileSync(join(dir, ".gitattributes"), "*.txt diff=hidden\n");
    git(dir, ["config", "diff.hidden.textconv", "false"]);
    git(dir, ["config", "diff.hidden.command", "false"]);
    writeFileSync(join(dir, "file.txt"), canary);
    commit(dir, "control");
    writeFileSync(join(dir, "file.txt"), "safe\n");
    commit(dir, "remove");
    const saved = { ...process.env };
    try {
      process.env["GIT_DIR"] = join(dir, "absent");
      process.env["GIT_CONFIG_COUNT"] = "1";
      process.env["GITLEAKS_CONFIG_TOML"] = "invalid";
      assert.equal(await scan(dir), 1);
    } finally {
      process.env = saved;
    }
  });
});
