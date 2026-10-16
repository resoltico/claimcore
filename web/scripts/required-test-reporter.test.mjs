import assert from "node:assert/strict";
import test from "node:test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

const web = fileURLToPath(new URL("..", import.meta.url));
const reporter = fileURLToPath(new URL("./required-test-reporter.mjs", import.meta.url));
const vitest = join(web, "node_modules/vitest/vitest.mjs");

/** @param {string} directory @param {string} source @param {boolean} [allowOnly] */
function runFixture(directory, source, allowOnly = false) {
  writeFileSync(join(directory, "fixture.test.ts"), source);
  writeFileSync(
    join(directory, "vitest.config.mjs"),
    `export default { test: {
    root: ${JSON.stringify(directory)}, include: ['fixture.test.ts'], environment: 'node',
    allowOnly: ${allowOnly}, passWithNoTests: true, retry: 0, maxWorkers: 1, reporters: ["default", ${JSON.stringify(reporter)}],
  }};`,
  );
  const result = spawnSync(
    process.execPath,
    [vitest, "run", "--config", join(directory, "vitest.config.mjs")],
    {
      cwd: web,
      encoding: "utf8",
      timeout: 12000,
      maxBuffer: 1048576,
    },
  );
  assert.equal(result.error, undefined);
  assert.equal(result.signal, null);
  return result;
}

test("actual runner refuses incomplete and focused declarations regardless of syntax", () => {
  const parent = join(web, "artifacts");
  mkdirSync(parent, { recursive: true });
  const directory = mkdtempSync(join(parent, "required-tests-"));
  try {
    const passed = runFixture(
      directory,
      'import * as api from "vitest"; api.test("complete",()=>{});',
    );
    assert.equal(passed.status, 0, passed.stderr);
    const focused = runFixture(
      directory,
      'import {test} from "vitest"; test.only("focused",()=>{});',
      true,
    );
    assert.notEqual(focused.status, 0);
    assert.match(focused.stdout + focused.stderr, /Required test completion refused/u);
    for (const declaration of [
      "/* empty collection */",
      'api.test.skip("excluded",()=>{})',
      'api.test.only("focused",()=>{})',
      'api.test.todo("pending")',
      'api.test.skipIf(true).each([1])("conditional",()=>{})',
      'api.describe.skip("suite",()=>{ api.test("nested",()=>{}) })',
      'api.test("dynamic", context=>context.skip())',
      'let calls=0; api.test("retried",{retry:1},()=>{if(calls++===0) throw new Error("once")})',
      'api.describe.skip("empty",()=>{}); api.test("complete",()=>{})',
    ]) {
      const result = runFixture(directory, `import * as api from "vitest"; ${declaration};`);
      assert.notEqual(result.status, 0, declaration);
      assert.match(
        result.stdout + result.stderr,
        /Required test completion refused|allowOnly/iu,
        declaration,
      );
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
