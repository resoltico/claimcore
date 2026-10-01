import assert from "node:assert/strict";
import test from "node:test";
import { buildInputs } from "./build.mjs";
import { parseSelection, selectSuites } from "./selection.mjs";

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
