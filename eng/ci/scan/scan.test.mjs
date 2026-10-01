import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { installTool } from "../tools.mjs";
import { scanArtifacts } from "./artifacts.mjs";
import { parseNulPaths, resolveSourceFile, scanSource } from "./source.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const gitleaks = await installTool(root, "gitleaks");
const canary = [
  `aws_access_key_id = ${"AK"}${"IA7JQ4N2P6R8T0V3X5"}`,
  `aws_secret_access_key = ${"7Yk3pQ9v"}${"L2mN8cR4"}${"tW6xZ1aB"}${"5dF0hJ7s"}${"K9uE3iO6"}`,
].join("\n");
const annotated = `${canary.replace("\n", " # gitleaks:allow\n")} # gitleaks:allow`;
const gitEnvironment = { ...process.env };
for (const name of Object.keys(gitEnvironment)) {
  if (name.startsWith("GIT_")) {
    delete gitEnvironment[name];
  }
}

/** @returns {{ dir: string, write: (relative: string, content: string) => void, done: () => void }} */
function sandbox({ outsideRepository = false } = {}) {
  mkdirSync(join(root, "artifacts"), { recursive: true });
  // A source fixture must not sit inside this repository's own worktree; an artifact fixture must
  // sit under a link-free path, which the system temporary directory is not on every platform.
  const dir = outsideRepository
    ? mkdtempSync(join(tmpdir(), "claimcore-source-fixture-"))
    : mkdtempSync(join(root, "artifacts/scan-test-"));
  return {
    dir,
    write(relative, content) {
      const path = join(dir, relative);
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, content);
    },
    done: () => rmSync(dir, { recursive: true, force: true }),
  };
}

/** @param {string} dir @param {string[]} args */
const git = (dir, args) =>
  execFileSync("git", ["-C", dir, ...args], { env: gitEnvironment, stdio: "pipe" });

/**
 * @param {string} dir
 * @returns {Promise<number>}
 */
async function sourceStatus(dir) {
  const quiet = () => undefined;
  return scanSource({ root: dir, gitleaks, stdout: quiet, stderr: quiet });
}

test("source inventory parsing is strict", () => {
  assert.deepEqual(parseNulPaths("a\0b/c\0"), ["a", "b/c"]);
  assert.deepEqual(parseNulPaths(""), []);
  assert.throws(() => parseNulPaths("a\0b"), /non-terminated/u);
  assert.throws(() => parseNulPaths("a\0\0"), /empty source path/u);
});

test("source paths must be exact, link-free regular files inside the root", () => {
  const files = sandbox();
  try {
    files.write("src/file.txt", "x");
    const inside = resolve(files.dir);
    assert.equal(resolveSourceFile(inside, "src/file.txt"), join(inside, "src/file.txt"));
    for (const unsafe of [
      "",
      "/etc/passwd",
      "src\\file.txt",
      "src/../src/file.txt",
      "src//file.txt",
      "src/File.txt",
      "src",
    ]) {
      assert.throws(() => resolveSourceFile(inside, unsafe), /./u, unsafe);
    }
    symlinkSync(join(inside, "src/file.txt"), join(inside, "link.txt"));
    assert.throws(() => resolveSourceFile(inside, "link.txt"), /symbolic links/u);
  } finally {
    files.done();
  }
});

test("the source scan covers force-tracked and unignored files, never ignored local state", async () => {
  const files = sandbox({ outsideRepository: true });
  try {
    files.write(".gitignore", ".local/\nignored-secret.txt\n");
    files.write("src/safe.txt", "synthetic safe source\n");
    files.write(".local/ignored.txt", canary);
    assert.equal(await sourceStatus(files.dir), 0, "ignored local state is excluded without git");

    files.write("src/ordinary-secret.txt", canary);
    assert.notEqual(await sourceStatus(files.dir), 0, "ordinary canary detected");
    unlinkSync(join(files.dir, "src/ordinary-secret.txt"));

    files.write("src/inline-allow.txt", annotated);
    assert.notEqual(await sourceStatus(files.dir), 0, "inline allow comment does not bypass");
    unlinkSync(join(files.dir, "src/inline-allow.txt"));

    git(files.dir, ["init", "--quiet"]);
    git(files.dir, ["add", ".gitignore", "src/safe.txt"]);
    assert.equal(await sourceStatus(files.dir), 0, "ignored untracked state is excluded with git");

    files.write("ignored-secret.txt", canary);
    git(files.dir, ["add", "--force", "ignored-secret.txt"]);
    assert.notEqual(await sourceStatus(files.dir), 0, "force-tracked ignored canary detected");
  } finally {
    files.done();
  }
});

test("a repository-local scanner configuration is refused", async () => {
  const files = sandbox({ outsideRepository: true });
  try {
    files.write("a.txt", "x");
    files.write(".gitleaks.toml", "title = 'x'\n");
    await assert.rejects(() => sourceStatus(files.dir), /forbidden/u);
  } finally {
    files.done();
  }
});

/**
 * @param {string[]} paths
 * @param {{ gitleaks?: () => Promise<string> | string }} [options]
 * @returns {Promise<{ status: number, output: string }>}
 */
async function artifactScan(paths, options = {}) {
  let output = "";
  const record = (/** @type {string} */ text) => {
    output += text;
  };
  const status = await scanArtifacts(paths, {
    gitleaks: options.gitleaks ?? (() => gitleaks),
    stdout: record,
    stderr: record,
  });
  return { status, output };
}

test("artifact scan refuses absent, missing, secret-bearing and overridden inputs", async () => {
  const files = sandbox();
  try {
    files.write("fixture/safe.txt", "synthetic artifact without a credential\n");
    const safe = join(files.dir, "fixture/safe.txt");

    const none = await artifactScan([]);
    assert.notEqual(none.status, 0);
    assert.match(none.output, /at INPUT/u);
    assert.notEqual((await artifactScan([safe, join(files.dir, "missing.txt")])).status, 0);
    assert.equal((await artifactScan([safe])).status, 0);

    const unavailable = await artifactScan([safe], {
      gitleaks: () => {
        throw new Error("synthetic");
      },
    });
    assert.notEqual(unavailable.status, 0);
    assert.match(unavailable.output, /at SCANNER_ACQUISITION/u);

    files.write("fixture/ordinary.txt", canary);
    const ordinary = await artifactScan([safe, join(files.dir, "fixture/ordinary.txt")]);
    assert.notEqual(ordinary.status, 0);
    assert.doesNotMatch(
      ordinary.output,
      /AKIA|7Yk3pQ9v/u,
      "scanner output never carries secret bytes",
    );
    unlinkSync(join(files.dir, "fixture/ordinary.txt"));

    files.write("fixture/inline-allow.txt", annotated);
    assert.notEqual((await artifactScan([join(files.dir, "fixture/inline-allow.txt")])).status, 0);
    unlinkSync(join(files.dir, "fixture/inline-allow.txt"));

    files.write("fixture/.gitleaksignore", "synthetic override\n");
    assert.notEqual((await artifactScan([join(files.dir, "fixture")])).status, 0);
  } finally {
    files.done();
  }
});

test("artifact scan refuses links and empty trees", async () => {
  const files = sandbox();
  try {
    files.write("real/safe.txt", "ok\n");
    symlinkSync(join(files.dir, "real"), join(files.dir, "linked"));
    assert.notEqual((await artifactScan([join(files.dir, "linked")])).status, 0);
    mkdirSync(join(files.dir, "empty"));
    assert.notEqual((await artifactScan([join(files.dir, "empty")])).status, 0);
  } finally {
    files.done();
  }
});

test("artifact scan detects a credential inside an archive", async () => {
  const files = sandbox();
  try {
    files.write("archive-source/credential.txt", canary);
    mkdirSync(join(files.dir, "out"));
    execFileSync("tar", [
      "-czf",
      join(files.dir, "out/nested.tar.gz"),
      "-C",
      join(files.dir, "archive-source"),
      "credential.txt",
    ]);
    assert.notEqual((await artifactScan([join(files.dir, "out")])).status, 0);
  } finally {
    files.done();
  }
});

test("a tracked file deleted from the working tree is not scanned", async () => {
  const files = sandbox({ outsideRepository: true });
  try {
    files.write("src/safe.txt", "synthetic safe source\n");
    files.write("src/doomed.txt", canary);
    git(files.dir, ["init", "--quiet"]);
    git(files.dir, ["add", "."]);
    assert.notEqual(
      await sourceStatus(files.dir),
      0,
      "the tracked canary is detected while present",
    );
    unlinkSync(join(files.dir, "src/doomed.txt"));
    assert.equal(await sourceStatus(files.dir), 0, "its deletion leaves nothing to scan");
  } finally {
    files.done();
  }
});
