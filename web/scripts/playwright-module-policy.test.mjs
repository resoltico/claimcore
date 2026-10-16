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
  mkdirSync(join(target, "src/protocol"), { recursive: true });
  mkdirSync(join(target, "src/api"), { recursive: true });
  copyFileSync(join(web, "src/api/responseBytes.ts"), join(target, "src/api/responseBytes.ts"));
  mkdirSync(join(directory, "contracts"));
  symlinkSync(join(web, "node_modules"), join(target, "node_modules"), "dir");
  for (const name of ["browser-contract.ts", "endpoint-catalog.ts"]) {
    copyFileSync(join(repository, "contracts", name), join(directory, "contracts", name));
  }
  for (const name of ["schema.ts", "strictJson.ts", "jsonMemberNames.ts"]) {
    copyFileSync(join(web, "src/protocol", name), join(target, "src/protocol", name));
  }
  writeFileSync(join(target, "package.json"), JSON.stringify({ type: "module", private: true }));
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
    'import {isRequest} from "../../contracts/browser-contract"; import {strictJsonBytes} from "../src/api/responseBytes"; import {test,expect} from "@playwright/test"; test("generated protocol module",()=>{expect(isRequest("case.list",strictJsonBytes(new TextEncoder().encode(JSON.stringify({limit:1}))))).toBe(true);});',
  );
  return target;
};
test("generated browser modules require their emitted ESM package boundary", () => {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-module-boundary-"));
  try {
    const target = fixture(directory);
    const missing = discover(target, []);
    assert.equal(missing.status, 1);
    assert.match(
      missing.stdout + missing.stderr,
      /SyntaxError:.*(?:does not provide an export|Cannot use import statement outside a module)/u,
    );
    copyFileSync(
      join(repository, "contracts/package.json"),
      join(directory, "contracts/package.json"),
    );
    const present = discover(target, []);
    assert.equal(present.status, 0, present.stderr);
    assert.match(present.stdout, /Total: 1 test in 1 file/u);
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
