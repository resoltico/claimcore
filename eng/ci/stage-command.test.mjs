import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { commandFor } from "./stage-command.mjs";
import { installTool } from "./tools.mjs";

const root = resolve(import.meta.dirname, "../..");
const quality = /** @type {{stages: import("./types.mjs").Stage[]}} */ (
  JSON.parse(readFileSync(join(root, "eng/ci/stage-plans/quality.json"), "utf8"))
);
const stage = quality.stages.find((item) => item.id === "actionlint");
assert.ok(stage);

test("actionlint admits both workflow suffixes in a Gitless export and detects an invalid YAML workflow", async () => {
  const scratch = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-actionlint-source-"));
  const actionlint = await installTool(root, "actionlint");
  let passed = false;
  try {
    mkdirSync(join(scratch, ".github/workflows"), { recursive: true });
    const valid =
      "name: Fixture\non: push\npermissions: {}\njobs:\n  test:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo admitted\n";
    writeFileSync(join(scratch, ".github/workflows/first.yml"), valid);
    writeFileSync(join(scratch, ".github/workflows/second.yaml"), valid);
    writeFileSync(join(scratch, "unrelated.yml"), "invalid unrelated file");
    const [command, ...args] = commandFor(stage, "synthetic", scratch);
    assert.equal(command, "actionlint");
    assert.deepEqual(args, [
      "-color",
      ".github/workflows/first.yml",
      ".github/workflows/second.yaml",
    ]);
    const invoke = () => spawnSync(actionlint, args, { cwd: scratch, encoding: "utf8" });
    assert.equal(invoke().status, 0);
    writeFileSync(
      join(scratch, ".github/workflows/second.yaml"),
      valid.replace("runs-on:", "unsupported-runs-on:"),
    );
    const invalid = invoke();
    assert.notEqual(invalid.status, 0);
    assert.match(invalid.stdout + invalid.stderr, /second\.yaml/u);
    passed = true;
  } finally {
    if (passed) {
      rmSync(scratch, { recursive: true });
    } else {
      process.stderr.write(`Actionlint failure fixture retained: ${scratch}.\n`);
    }
  }
});
