import assert from "node:assert/strict";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { compareNames, normalizeNames, parseInventory, renderInventory } from "./inventory.mjs";
import { inventoryPath, loadSuites } from "./registry.mjs";

const repository = fileURLToPath(new URL("../../..", import.meta.url));

/** @param {string} path */
const readText = (path) => readFileSync(path, "utf8");

test("inventories are sorted, unique and newline-terminated", () => {
  assert.equal(renderInventory(["b", "a"]), "a\nb\n");
  assert.deepEqual(parseInventory("a\nb\n"), ["a", "b"]);
});

test("an empty, blank, multi-line or duplicated name is refused", () => {
  assert.throws(() => normalizeNames([]), /empty/u);
  assert.throws(() => normalizeNames(["a", " "]), /non-empty/u);
  assert.throws(() => normalizeNames(["a\nb"]), /single lines/u);
  assert.throws(() => normalizeNames(["a", "a"]), /not unique/u);
});

test("a rename shows as one missing and one unexpected name", () => {
  assert.deepEqual(compareNames(["old", "same"], ["new", "same"]), {
    missing: ["old"],
    unexpected: ["new"],
  });
});

test("the real registry validates and every committed inventory exists", () => {
  for (const suite of loadSuites(repository)) {
    assert.ok(parseInventory(readText(join(repository, inventoryPath(suite)))).length > 0);
  }
});

test("registry validation names every problem", () => {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-registry-"));
  try {
    cpSync(join(repository, "config"), join(directory, "config"), { recursive: true });
    writeFileSync(
      join(directory, "config/test-suites.json"),
      JSON.stringify({
        schemaVersion: 2,
        suites: [{ id: "Bad", kind: "x", platforms: ["plan9"] }],
      }),
    );
    assert.throws(() => loadSuites(directory), /kebab-case[\s\S]*unknown kind[\s\S]*platforms/u);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
