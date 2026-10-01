import assert from "node:assert/strict";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { buildMatrix, runners } from "./matrix.mjs";
import { loadSuites } from "./registry.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

test("every ungrouped suite appears once per supported platform on a pinned runner", () => {
  const { include } = buildMatrix(loadSuites(root));
  const suites = loadSuites(root).filter(
    (suite) => suite.kind === "dotnet" && suite.group === undefined,
  );
  assert.equal(
    include.length,
    suites.reduce((sum, suite) => sum + suite.platforms.length, 0),
  );
  assert.equal(
    new Set(include.map((entry) => `${entry.suite}/${entry.platform}`)).size,
    include.length,
  );
  for (const { os } of include) {
    assert.ok(/** @type {string[]} */ (Object.values(runners)).includes(os));
    assert.doesNotMatch(os, /-latest$/u);
  }
});

test("grouped suites and an empty selection are not part of the matrix", () => {
  assert.ok(!buildMatrix(loadSuites(root)).include.some((entry) => entry.suite === "integration"));
  assert.throws(() => buildMatrix([]), /No registered suite/u);
});
