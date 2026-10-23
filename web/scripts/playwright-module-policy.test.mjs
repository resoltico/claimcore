import assert from "node:assert/strict";
import test from "node:test";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
const web = fileURLToPath(new URL("..", import.meta.url));
const repository = resolve(web, "..");
const cli = join(web, "node_modules/playwright/cli.js");
/** @param {string} cwd @param {string[]} args */
const discover = (cwd, args) =>
  spawnSync(process.execPath, [cli, "test", "--list", ...args], {
    cwd,
    encoding: "utf8",
    timeout: 30000,
  });
/** @param {string} directory */
const fixture = (directory) => {
  const target = join(directory, "web");
  mkdirSync(join(target, "e2e"), { recursive: true });
  mkdirSync(join(directory, "contracts"));
  symlinkSync(join(web, "node_modules"), join(target, "node_modules"), "dir");
  copyFileSync(join(repository, "contracts/client.ts"), join(directory, "contracts/client.ts"));
  writeFileSync(join(directory, "package.json"), JSON.stringify({ type: "module", private: true }));
  writeFileSync(
    join(target, "tsconfig.json"),
    JSON.stringify({ compilerOptions: { paths: { "@contracts/*": ["../contracts/*"] } } }),
  );
  writeFileSync(
    join(target, "playwright.config.ts"),
    'export default {testDir:"./e2e",reporter:"list"};',
  );
  writeFileSync(
    join(target, "e2e/protocol.spec.ts"),
    'import {endpoints} from "../../contracts/client"; import {test,expect} from "@playwright/test"; test("generated protocol data",()=>{expect(endpoints.some(endpoint=>endpoint.id==="case.list")).toBe(true);});',
  );
  return target;
};
test("generated ESM data loads in its consumer and missing exports refuse", () => {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-module-boundary-"));
  try {
    const target = fixture(directory);
    const present = discover(target, []);
    assert.equal(present.status, 0, present.stderr);
    assert.match(present.stdout, /Total: 1 test in 1 file/u);
    writeFileSync(join(directory, "contracts/client.ts"), "export {};\n");
    const missing = discover(target, []);
    assert.equal(missing.status, 1);
    assert.match(missing.stdout + missing.stderr, /does not provide an export|undefined/u);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
test("published browser specifications load for every configured engine without launching fixtures", () => {
  const result = discover(web, []);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  for (const engine of ["chromium", "firefox", "webkit"]) {
    assert.match(result.stdout, new RegExp(`\\[${engine}\\]`, "u"));
  }
  assert.doesNotMatch(result.stdout, /Total: 0 tests/u);
});
