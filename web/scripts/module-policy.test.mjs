import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { moduleViolations } from "./module-policy.mjs";
const root = new URL("..", import.meta.url).pathname;
/** @param {string} directory @returns {string[]} */
function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((item) => {
    if (item.isDirectory()) {
      return files(join(directory, item.name));
    }
    if (/\.(ts|tsx|js|mjs|json)$/u.test(item.name)) {
      return [join(directory, item.name)];
    }
    return [];
  });
}
test("renderer module boundaries resolve aliases, barrel exports and dynamic imports", () => {
  for (const source of [
    'import "@view/Panel"',
    'export * from "@view/Panel"',
    'void import("@view/Panel")',
  ]) {
    assert.notEqual(
      moduleViolations(
        { "src/interaction/state.ts": source, "src/views/Panel.ts": "export {};" },
        { "@view/*": ["src/views/*"] },
      ).length,
      0,
    );
  }
  assert.deepEqual(
    moduleViolations(
      {
        "src/views/Panel.ts": 'import "../interaction/state"',
        "src/interaction/state.ts": "export {};",
      },
      {},
    ),
    [],
  );
  assert.notEqual(
    moduleViolations(
      {
        "src/interaction/state.ts": 'import "./barrel"',
        "src/interaction/barrel.ts": 'export * from "../views/Panel"',
        "src/views/Panel.ts": "export {};",
      },
      {},
    ).length,
    0,
  );
  assert.notEqual(
    moduleViolations({ "src/api/a.ts": 'import "./b"', "src/api/b.ts": 'import "./a"' }, {}).length,
    0,
  );
});
test("production renderer module inventory and boundaries are complete", () => {
  const source = Object.fromEntries(
    [...files(join(root, "src")), ...files(join(root, "../contracts"))].map((file) => [
      relative(root, file),
      readFileSync(file, "utf8"),
    ]),
  );
  const config = JSON.parse(readFileSync(join(root, "tsconfig.base.json"), "utf8"));
  assert.deepEqual(moduleViolations(source, config.compilerOptions.paths), []);
});

test("transport facade and generated protocol purity reject representative bypasses", () => {
  for (const source of [
    'import "@io/transport"',
    'export * from "@io/transport"',
    'void import("@io/transport")',
  ]) {
    assert.notEqual(
      moduleViolations(
        { "src/views/view.ts": source, "src/api/transport.ts": "export {};" },
        { "@io/*": ["src/api/*"] },
      ).length,
      0,
    );
  }
  for (const source of [
    'import React from "react"',
    'import "node:fs"',
    "new Intl.NumberFormat()",
    "Date.now()",
    "crypto.randomUUID()",
    'fetch("/api")',
  ]) {
    assert.notEqual(moduleViolations({ "../contracts/generated.ts": source }, {}).length, 0);
  }
  assert.deepEqual(
    moduleViolations(
      { "src/api/service.ts": 'import "./transport"', "src/api/transport.ts": "export {};" },
      {},
    ),
    [],
  );
  assert.deepEqual(
    moduleViolations(
      {
        "../contracts/generated.ts": 'import "../web/src/protocol/scalar"',
        "src/protocol/scalar.ts": 'new Date("2026-01-01T00:00:00Z")',
      },
      {},
    ),
    [],
  );
});

test("protocol purity follows indirect helpers and JavaScript modules", () => {
  for (const extension of ["ts", "tsx", "js", "mjs"]) {
    for (const platform of [
      'import "react"',
      'import "node:fs"',
      "Date.now()",
      "globalThis.Date.now()",
      'globalThis.fetch("/")',
      "performance.now()",
    ]) {
      const sources = {
        "../contracts/generated.ts": 'export * from "../web/src/helper"',
        [`src/helper.${extension}`]: 'export * from "./hooks/indirect"',
        [`src/hooks/indirect.${extension}`]: platform,
      };
      assert.notEqual(moduleViolations(sources, {}).length, 0, `${extension}: ${platform}`);
    }
  }
  assert.deepEqual(
    moduleViolations(
      {
        "../contracts/generated.ts": 'export * from "../web/src/helper.mjs"',
        "src/helper.mjs": "export const exact = (value) => value;",
      },
      {},
    ),
    [],
  );
});

test("protocol JSON imports require inventoried valid data", () => {
  const code = 'import data from "../../../contracts/openapi.json" with {type:"json"};';
  const file = "src/protocol/client.ts";
  assert.deepEqual(moduleViolations({ [file]: code, "../contracts/openapi.json": "{}" }, {}), []);
  assert.notEqual(moduleViolations({ [file]: code }, {}).length, 0);
  assert.throws(() =>
    moduleViolations(
      { [file]: code, "../contracts/openapi.json": "export const execute=()=>{};" },
      {},
    ),
  );
});
