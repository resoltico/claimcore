import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { buildInputs } from "./build.mjs";
import { parseSelection, requireCompletePropertyProfile, selectSuites } from "./selection.mjs";

/** @type {import("./registry.mjs").Suite[]} */
const suites = [
  { id: "unit", kind: "dotnet", project: "tests/Unit/Unit.fsproj", platforms: ["linux"] },
  {
    id: "database",
    kind: "dotnet",
    project: "tests/Database/Database.fsproj",
    platforms: ["linux"],
    group: "postgres",
  },
  { id: "browser", kind: "playwright", platforms: ["linux"] },
];

test("option values never become suite IDs", () => {
  const selection = parseSelection([
    "run",
    "unit",
    "--parallel",
    "2",
    "--results-root",
    "artifacts/result",
    "--build",
  ]);
  assert.deepEqual(selection.ids, ["unit"]);
  assert.equal(selection.options["parallel"], "2");
  assert.deepEqual(
    selectSuites(suites, selection, "linux").map((suite) => suite.id),
    ["unit"],
  );
});

test("unknown, empty, unsupported, repeated and conflicting selections fail before work", () => {
  for (const argv of [
    ["run"],
    ["run", "unknown"],
    ["run", "browser"],
    ["run", "--group", "missing"],
  ]) {
    assert.throws(() => selectSuites(suites, parseSelection(argv), "linux"));
  }
  for (const argv of [
    ["run", "unit", "unit"],
    ["run", "--parallel"],
    ["run", "--typo"],
    ["run", "unit", "--group", "postgres"],
    ["run", "--build", "--build"],
  ]) {
    assert.throws(() => parseSelection(argv));
  }
});

test("shared prerequisites are built once per configuration and property set", () => {
  const requirements = [];
  for (const suite of suites.slice(0, 2)) {
    requirements.push({ ...suite, build: ["src/Cli/Cli.fsproj"] });
  }
  const inputs = buildInputs(requirements);
  assert.equal(inputs.filter((input) => input.project === "src/Cli/Cli.fsproj").length, 1);
  assert.equal(inputs.length, 3);
});

test("complete evidence refuses diagnostic property inputs and retains full profiles", () => {
  for (const environment of [
    { CLAIMCORE_PROPERTY_PROFILE: "recheck" },
    { CLAIMCORE_PROPERTY_RECHECK_ID: "CC-PROP-FINGERPRINT-001" },
    { CLAIMCORE_PROPERTY_RECHECK_TOKEN: "synthetic-token" },
  ]) {
    assert.throws(
      () => requireCompletePropertyProfile(environment),
      /Diagnostic property rechecks/u,
    );
  }
  for (const environment of [
    {},
    { CLAIMCORE_PROPERTY_PROFILE: "required", CLAIMCORE_PROPERTY_RECHECK_ID: "" },
    { CLAIMCORE_PROPERTY_PROFILE: "extended", CLAIMCORE_PROPERTY_BASE_SEED: "42" },
  ]) {
    assert.doesNotThrow(() => requireCompletePropertyProfile(environment));
  }
});

test("the required runner rejects recheck before restoring or creating evidence", () => {
  const root = fileURLToPath(new URL("../../..", import.meta.url));
  const results = `artifacts/recheck-policy-${randomUUID()}`;
  const child = spawnSync(
    process.execPath,
    ["eng/ci/suites/suite.mjs", "run", "fuzz", "--build", "--results-root", results],
    {
      cwd: root,
      env: { ...process.env, CLAIMCORE_PROPERTY_PROFILE: "recheck" },
      encoding: "utf8",
      timeout: 10_000,
    },
  );
  assert.equal(child.error, undefined);
  assert.equal(child.status, 1);
  assert.match(child.stderr, /Diagnostic property rechecks/u);
  assert.doesNotMatch(child.stdout, /Determining projects to restore|Running tests from/u);
  assert.equal(existsSync(new URL(`../../../${results}`, import.meta.url)), false);
});
