import assert from "node:assert/strict";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { checkTestSuites } from "./check-test-suites.mjs";

const repository = fileURLToPath(new URL("../..", import.meta.url));
const files = [
  "config/test-suites.json",
  ".github/workflows/verify-unit.yml",
  ".github/workflows/verify-properties.yml",
  ".github/workflows/verify-documentation.yml",
  "eng/test-partitions.json",
  "eng/Invoke-PostgresQualifications.ps1",
];

/**
 * @param {string} file
 * @param {(text: string) => string} change
 * @returns {string[]}
 */
function withChange(file, change) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-suites-"));
  try {
    for (const path of files) {
      cpSync(join(repository, path), join(root, path), { recursive: true });
    }
    writeFileSync(join(root, file), change(readFileSync(join(root, file), "utf8")));
    return checkTestSuites(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("the repository's workflows and runners agree with the suite registry", () => {
  assert.deepEqual(checkTestSuites(repository), []);
});

/** @type {Array<[string, string, (text: string) => string, RegExp]>} */
const drift = [
  [
    "a matrix count",
    ".github/workflows/verify-unit.yml",
    (t) => t.replace("expected: 334", "expected: 335"),
    /disagrees/u,
  ],
  [
    "a matrix coverage role",
    ".github/workflows/verify-unit.yml",
    (t) => t.replace("coverage: true", "coverage: false"),
    /disagrees/u,
  ],
  [
    "a repeated property-run count",
    ".github/workflows/verify-properties.yml",
    (t) => t.replace("=334", "=333"),
    /repeats minimum counts/u,
  ],
  [
    "a documentation count",
    ".github/workflows/verify-documentation.yml",
    (t) => t.replace("=78", "=77"),
    /repeats minimum counts/u,
  ],
  [
    "a registry count",
    "config/test-suites.json",
    (t) => t.replace('"expected": 372', '"expected": 371'),
    /do not sum/u,
  ],
  [
    "a repeated runner table",
    "eng/Invoke-PostgresQualifications.ps1",
    (t) => `${t}\n@{ Expected = 372 }\n`,
    /repeats a test count/u,
  ],
];
for (const [name, file, change, message] of drift) {
  test(`${name} is refused`, () => {
    assert.ok(withChange(file, change).some((error) => message.test(error)));
  });
}
