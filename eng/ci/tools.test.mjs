import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { collectChecks } from "./doctor.mjs";
import { loadTools } from "./tools.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
/** @param {string} path @returns {any} */
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));

test("the Node and npm pins agree across every file that states them", () => {
  const node = readFileSync(join(root, ".node-version"), "utf8").trim();
  for (const manifest of ["web/package.json", "eng/package.json"]) {
    const { engines, packageManager } = readJson(manifest);
    assert.equal(engines.node, node, `${manifest} engines.node`);
    assert.equal(packageManager, `npm@${engines.npm}`, `${manifest} packageManager`);
  }
  assert.equal(readJson("web/package.json").engines.npm, readJson("eng/package.json").engines.npm);
});

test("every pinned download names its version, a digest and the platforms CI uses", () => {
  const tools = loadTools(root);
  assert.ok(Object.keys(tools).length > 0);
  for (const [name, tool] of Object.entries(tools)) {
    for (const platform of ["linux-x64"]) {
      assert.ok(tool.assets[platform], `${name} has no ${platform} asset`);
    }
    for (const [platform, asset] of Object.entries(tool.assets)) {
      assert.match(asset.sha256, /^[0-9a-f]{64}$/u, `${name} ${platform} digest`);
      assert.ok(asset.url.startsWith("https://github.com/"), `${name} ${platform} source`);
      assert.ok(asset.url.includes(tool.version), `${name} ${platform} url names the version`);
    }
  }
});

test("the doctor reads each required version from the file that owns it", () => {
  const required = collectChecks().filter((check) => check.required);
  assert.deepEqual(
    required.map((check) => check.tool),
    ["dotnet", "node", "npm", "git"],
  );
  assert.equal(required[0]?.expected, readJson("global.json").sdk.version);
});
