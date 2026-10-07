import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { Report } from "./model.mjs";
import { checkOxlintLimits } from "./policies/oxlint.mjs";
import { findings, validEntry, withTree } from "./test-support.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const base = JSON.parse(readFileSync(join(root, "config/oxlint.json"), "utf8"));

/** @param {Record<string,unknown>} rules @param {string} source */
function nativeProbe(rules, source) {
  return withTree({ "probe.mjs": source }, (directory) => {
    const config = { ...base, overrides: [{ files: ["**/*.mjs"], rules }] };
    writeFileSync(join(directory, "config.json"), JSON.stringify(config));
    const result = spawnSync(
      process.execPath,
      [
        join(root, "eng/node_modules/oxlint/bin/oxlint"),
        "--config",
        join(directory, "config.json"),
        join(directory, "probe.mjs"),
      ],
      { encoding: "utf8" },
    );
    const report = new Report();
    checkOxlintLimits(directory, "config.json", report);
    return { status: result.status, output: result.stdout + result.stderr, errors: report.errors };
  });
}

test("real Oxlint ceiling aliases cannot turn an over-complex function into admitted source", () => {
  const branches = Array.from(
    { length: 13 },
    (_, index) => `if (x === ${index}) { return ${index}; }`,
  );
  const source = `export function probe(x) {\n${branches.join("\n")}\nreturn -1;\n}\n`;
  const strict = nativeProbe({}, source);
  assert.equal(strict.status, 1);
  assert.match(strict.output, /complexity of 14/u);
  assert.deepEqual(strict.errors, []);
  for (const setting of ["off", ["error", { max: 999 }]]) {
    const bypass = nativeProbe({ "eslint/complexity": setting }, source);
    assert.equal(bypass.status, 0, "Native alias must reproduce the otherwise unchecked escape.");
    assert.match(bypass.errors.join("\n"), /eslint\/complexity/u);
  }
});

test("prefixed function-size, argument and statement overrides are pinned in every scope", () => {
  for (const name of ["max-lines", "max-lines-per-function", "max-params", "max-statements"]) {
    const errors = nativeProbe(
      { [`eslint/${name}`]: ["error", { max: 999 }] },
      "export const x = 1;\n",
    );
    assert.match(errors.errors.join("\n"), new RegExp(`eslint/${name}`, "u"));
  }
});

test("MSBuild, PowerShell and native C source files obey the same physical-line ceiling", () => {
  for (const extension of ["fsproj", "props", "targets", "ps1", "c", "h"]) {
    const path = `eng/probe.${extension}`;
    assert.deepEqual(findings({ [path]: "\n".repeat(300) }, []), []);
    assert.match(findings({ [path]: "\n".repeat(301) }, []).join("\n"), /301 physical lines/u);
  }
});

test("function, argument, statement, CSS and native rule names cannot be registered away", () => {
  for (const rule of [
    "eslint/max-params",
    "max-statements",
    "maxLinesInFunction",
    "cyclomaticComplexity",
    "C901",
    "PLR0913",
    "max-nesting-depth",
    "selector-max-specificity",
  ]) {
    const errors = findings({ [validEntry.file]: "" }, [{ ...validEntry, rules: [rule] }]);
    assert.match(errors.join("\n"), /non-suppressible/u, rule);
  }
});

test("FSharpLint and Ruff ceilings require positive integer values", () => {
  const fsharp = JSON.parse(readFileSync(join(root, "config/fsharplint.json"), "utf8"));
  fsharp.ignoreFiles = [];
  const python = readFileSync(join(root, "pyproject.toml"), "utf8");
  for (const value of [0, -1, 1.5]) {
    const altered = structuredClone(fsharp);
    altered.cyclomaticComplexity.config.maxComplexity = value;
    assert.match(
      findings({ "config/fsharplint.json": JSON.stringify(altered) }, []).join("\n"),
      /cyclomaticComplexity/u,
    );
    const alteredPython = python.replace(/max-complexity = 12/u, `max-complexity = ${value}`);
    assert.match(
      findings({ "pyproject.toml": alteredPython }, []).join("\n"),
      /McCabe complexity/u,
    );
  }
});
