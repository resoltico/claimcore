import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { executable } from "./executable.mjs";
import { copySource } from "./source-snapshot.mjs";
import { commandFor } from "./stage-command.mjs";
import { validatePlan } from "./stage-plan.mjs";
import { installTool } from "./tools.mjs";

const root = resolve(import.meta.dirname, "../..");
const quality = /** @type {import("./types.mjs").Plan} */ (
  JSON.parse(readFileSync(join(root, "eng/ci/stage-plans/quality.json"), "utf8"))
);
const python = quality.stages.filter((stage) => stage.appendFiles?.suffixes.includes(".py"));
const valid = '"""Synthetic source for Python qualification."""\n\nANSWER: int = 42\n';

/** @param {string} source @param {string[]} args */
function git(source, args) {
  assert.equal(spawnSync(executable("git"), args, { cwd: source, stdio: "ignore" }).status, 0);
}

/** @param {string} source */
function populate(source) {
  writeFileSync(join(source, ".gitignore"), "/artifacts/\n.venv/\n");
  for (const file of ["pyproject.toml", "uv.lock"]) {
    copyFileSync(join(root, file), join(source, file));
  }
  mkdirSync(join(source, "future"));
  writeFileSync(join(source, "source.py"), valid);
  writeFileSync(join(source, "future/__init__.py"), valid);
  writeFileSync(join(source, "future/check.py"), valid);
}

/** @param {string} source @param {string} uv @param {string} id */
function invoke(source, uv, id) {
  const stage = python.find((item) => item.id === id);
  assert.ok(stage);
  const [command, ...args] = commandFor(stage, "synthetic", source);
  assert.equal(command, "uv");
  return spawnSync(uv, args, { cwd: source, encoding: "utf8" });
}

/** @param {string} source @param {string} uv @param {boolean} gitless */
function qualify(source, uv, gitless) {
  const expected = ["future/__init__.py", "future/check.py", "source.py"];
  const copied = join(source, "artifacts/publication-inputs/source/eng/backup");
  mkdirSync(copied, { recursive: true });
  writeFileSync(join(copied, "Test-copy.py"), "import os\n\ndef broken():\n return 1\n");
  for (const stage of python) {
    assert.deepEqual(commandFor(stage, "synthetic", source).slice(stage.argv.length), expected);
  }
  if (gitless) {
    const lint = python.find((stage) => stage.id === "python-lint");
    assert.ok(lint);
    const walked = spawnSync(uv, lint.argv.slice(1), { cwd: source, encoding: "utf8" });
    assert.notEqual(walked.status, 0, "The unscoped command admits the generated bad copy.");
    assert.match(walked.stdout + walked.stderr, /Test-copy\.py/u);
  }
  for (const id of ["python-format", "python-lint"]) {
    const passed = invoke(source, uv, id);
    assert.equal(passed.status, 0, passed.stdout + passed.stderr);
  }
  writeFileSync(join(source, "future/check.py"), `${valid}\nimport os\n`);
  const lint = invoke(source, uv, "python-lint");
  assert.notEqual(lint.status, 0);
  assert.match(lint.stdout + lint.stderr, /F401/u);
  writeFileSync(join(source, "future/check.py"), valid.replace(" = 42", "=42"));
  const format = invoke(source, uv, "python-format");
  assert.notEqual(format.status, 0);
  assert.match(format.stdout + format.stderr, /future[/\\]check\.py/u);
}

test("Python stages register complete source selectors and refuse an empty inventory", () => {
  validatePlan(quality);
  assert.deepEqual(
    python.map((stage) => stage.id),
    ["python-format", "python-lint", "python-types", "python-limits"],
  );
  const empty = mkdtempSync(join(tmpdir(), "claimcore-python-empty-"));
  try {
    for (const stage of python) {
      assert.throws(() => commandFor(stage, "synthetic", empty), /matched no source/u);
    }
  } finally {
    rmSync(empty, { recursive: true });
  }
});

test("Ruff ignores generated source copies but rejects admitted defects in Git and Gitless roots", async () => {
  const uv = await installTool(root, "uv");
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-python-source-"));
  const source = join(scratch, "checkout");
  const snapshot = join(scratch, "snapshot");
  let passed = false;
  try {
    mkdirSync(source);
    mkdirSync(snapshot);
    populate(source);
    git(source, ["init", "--quiet"]);
    git(source, ["add", "."]);
    copySource(source, snapshot);
    qualify(source, uv, false);
    qualify(snapshot, uv, true);
    passed = true;
  } finally {
    if (passed) {
      rmSync(scratch, { recursive: true });
    } else {
      process.stderr.write(`Python source failure fixture retained: ${scratch}.\n`);
    }
  }
});
