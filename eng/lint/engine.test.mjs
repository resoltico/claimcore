import assert from "node:assert/strict";
import test from "node:test";
import { checkRepository } from "./engine.mjs";
import { registryText, validEntry, withTree } from "./test-support.mjs";

const source = "web/src/example.ts";
const suppressed =
  "// lint-exception: LX-0001\n// oxlint-disable-next-line no-console\nconsole.log(1);\n";

/**
 * @param {Record<string, string>} files
 * @param {Record<string, unknown>[]} exceptions
 * @returns {string[]}
 */
function findings(files, exceptions) {
  return withTree(
    { "config/lint-exceptions.json": registryText(exceptions), ...files },
    (root) => checkRepository(root).report.errors,
  );
}

test("a registered inline suppression passes and every entry is accounted for", () => {
  assert.deepEqual(findings({ [source]: suppressed }, [validEntry]), []);
});

test("an inline suppression without a reference fails", () => {
  const errors = findings(
    { [source]: "// oxlint-disable-next-line no-console\nconsole.log(1);\n" },
    [validEntry],
  );
  assert.ok(
    errors.some((error) => /needs a nearby 'lint-exception: LX-0000' reference/.test(error)),
  );
  assert.ok(errors.some((error) => /Stale exception LX-0001/.test(error)));
});

test("a blanket suppression fails even with a reference", () => {
  const errors = findings({ [source]: "// lint-exception: LX-0001\n/* oxlint-disable */\n" }, [
    validEntry,
  ]);
  assert.ok(errors.some((error) => /blanket suppression/.test(error)));
});

/** @type {Array<[string, Record<string, unknown>, RegExp]>} */
const mismatches = [
  ["another file", { file: "web/src/other.ts" }, /is registered for web\/src\/other\.ts/],
  ["another tool", { tool: "tsc", rules: ["@ts-ignore"] }, /is registered for tsc/],
  ["a rule it does not list", { rules: ["no-debugger"] }, /does not list no-console/],
  ["a config kind", { kind: "config" }, /is not an inline exception/],
];
for (const [name, change, message] of mismatches) {
  test(`a reference to an entry registered for ${name} fails`, () => {
    const errors = findings({ [source]: suppressed, "web/src/other.ts": "" }, [
      { ...validEntry, ...change },
    ]);
    assert.ok(
      errors.some((error) => message.test(error)),
      errors.join("\n"),
    );
  });
}

test("a reference to an unknown id fails", () => {
  const errors = findings({ [source]: suppressed.replace("LX-0001", "LX-0042") }, [validEntry]);
  assert.ok(errors.some((error) => /unknown exception LX-0042/.test(error)));
});

test("an entry with no suppression left is stale", () => {
  const errors = findings({ [source]: "export const value = 1;\n" }, [validEntry]);
  assert.ok(errors.some((error) => /Stale exception LX-0001/.test(error)));
});

test("the entry count must equal the occurrences", () => {
  const twice = `${suppressed}${suppressed}`;
  assert.ok(
    findings({ [source]: twice }, [validEntry]).some((error) =>
      /covers 1 occurrence\(s\) but .* has 2/.test(error),
    ),
  );
  assert.deepEqual(findings({ [source]: twice }, [{ ...validEntry, count: 2 }]), []);
});

test("a configuration ignore needs an entry that names its exact target", () => {
  const files = { "web/.oxlintrc.json": '{ "ignorePatterns": ["gen/*.mjs"] }' };
  const config = {
    ...validEntry,
    id: "LX-0002",
    kind: "config",
    file: "web/.oxlintrc.json",
    rules: ["ignorePatterns:gen/*.mjs"],
  };
  const exceptionFindings = (/** @type {Record<string, unknown>[]} */ entries) =>
    findings(files, entries).filter((error) => !/must set/.test(error));
  assert.deepEqual(exceptionFindings([config]), []);
  assert.ok(
    exceptionFindings([]).some((error) =>
      /unregistered oxlint exception: ignorePatterns:gen\/\*\.mjs/.test(error),
    ),
  );
  assert.ok(exceptionFindings([{ ...config, rules: ["ignorePatterns:other/*.mjs"] }]).length > 0);
});

test("generated output is not scanned when the registry excludes it", () => {
  const generated = [
    {
      path: "web/dist/",
      generator: "the Vite production builder",
      reason: "Generated output that is never hand-authored.",
      owner: "project maintainers",
      reviewOn: "2999-01-01",
    },
  ];
  const files = {
    "config/lint-exceptions.json": registryText([], generated),
    "web/dist/a.mjs": "/* oxlint-disable */\n",
  };
  assert.deepEqual(
    withTree(files, (root) => checkRepository(root).report.errors),
    [],
  );
  assert.ok(findings({ "web/dist/a.mjs": "/* oxlint-disable */\n" }, []).length > 0);
});

test("a missing registry fails instead of passing an unchecked tree", () => {
  assert.match(
    withTree({ [source]: "" }, (root) => checkRepository(root).report.errors.join("\n")),
    /cannot be read/,
  );
});

/** @type {Array<[string, string, string, RegExp]>} */
const policies = [
  [
    "a focused F# test",
    "tests/A.Tests/T.fs",
    'let t = ftestCase "x" ignore\n',
    /focused or skipped F# test/,
  ],
  [
    "structural F# test formatting",
    "tests/A.Tests/T.fs",
    'let t = sprintf "%A" 1\n',
    /structural formatting/,
  ],
  ["a focused web test", "web/tests/a.test.ts", "it.only('x', () => {});\n", /focused, skipped/],
  [
    "a web test annotation",
    "web/e2e/a.spec.ts",
    "test.info().annotations.push({});\n",
    /test annotation/,
  ],
  [
    "a web retry",
    "web/tests/a.test.ts",
    "test.describe.configure({ retries: 2 });\n",
    /retry or expected-failure/,
  ],
  [
    "a skipped Python test",
    "eng/Test-X.py",
    "@unittest.skip('x')\n",
    /skipped or expected-failure Python/,
  ],
  [
    "a CI test filter",
    ".github/workflows/x.yml",
    "run: dotnet test --filter Name=x\n",
    /required-CI test filter/,
  ],
  [
    "a stage-plan test filter",
    "eng/ci/stage-plans/quality.json",
    '{ "argv": ["x", "--grep", "y"] }\n',
    /required-CI test filter/,
  ],
  [
    "a test-selection escape",
    "web/package.json",
    '{ "scripts": { "t": "vitest --passWithNoTests" } }\n',
    /test-selection escape/,
  ],
  [
    "Playwright retries",
    "web/playwright.config.ts",
    "export default { retries: 2 };\n",
    /zero-retry/,
  ],
  [
    "Vitest retries",
    "web/vite.config.ts",
    "export default { test: { retry: 1 } };\n",
    /zero-retry/,
  ],
  [
    "a file over the size limit",
    "web/src/big.ts",
    `${"const a = 1;\n".repeat(301)}`,
    /301 physical lines/,
  ],
];
for (const [name, path, text, message] of policies) {
  test(`${name} cannot be registered away`, () => {
    const errors = findings({ [path]: text }, []);
    assert.ok(
      errors.some((error) => message.test(error)),
      errors.join("\n"),
    );
  });
}

test("a file at exactly the size limit passes", () => {
  assert.deepEqual(findings({ "web/src/exact.ts": "const a = 1;\n".repeat(300) }, []), []);
});

test("F# linter weakening is rejected", () => {
  const good = {
    cyclomaticComplexity: { enabled: true, config: { maxComplexity: 12 } },
    ignoreFiles: [],
  };
  const errors = (/** @type {object} */ config) =>
    findings({ "config/fsharplint.json": JSON.stringify(config) }, []);
  assert.ok(
    errors({
      ...good,
      cyclomaticComplexity: { enabled: true, config: { maxComplexity: 30 } },
    }).some((e) => /weakens 'cyclomaticComplexity' to 30/.test(e)),
  );
  assert.ok(
    errors({ ...good, cyclomaticComplexity: { enabled: false } }).some((e) =>
      /disables 'cyclomaticComplexity'/.test(e),
    ),
  );
  assert.ok(
    errors({ ...good, ignoreFiles: ["src/x.fs"] }).some((e) =>
      /ignoreFiles must exactly match/.test(e),
    ),
  );
  assert.ok(
    errors({
      ...good,
      someRule: { enabled: true, config: { ignore: ["x"] } },
      redundantNewKeyword: { enabled: true },
    }).length > 0,
  );
});

test("oxlint size and complexity ceilings are fixed", () => {
  const config = (/** @type {number} */ complexity) =>
    JSON.stringify({
      rules: {
        complexity: ["error", { max: complexity }],
        "max-lines": ["error", { max: 300 }],
        "max-lines-per-function": ["error", { max: 50 }],
      },
    });
  assert.deepEqual(
    findings({ "web/.oxlintrc.json": config(12) }, []).filter((e) => /must set/.test(e)),
    [],
  );
  assert.ok(
    findings({ "web/.oxlintrc.json": config(13) }, []).some((e) => /must set 'complexity'/.test(e)),
  );
  assert.ok(
    findings({ "web/.oxlintrc.json": "{}" }, []).some((e) => /must set 'max-lines'/.test(e)),
  );
});

test("ruff must select every rule and mypy must run strict", () => {
  const toml = (/** @type {string} */ select, /** @type {string} */ strict) =>
    `[tool.ruff.lint]\nselect = ${select}\n[tool.ruff.lint.mccabe]\nmax-complexity = 12\n[tool.mypy]\nstrict = ${strict}\n`;
  assert.deepEqual(findings({ "pyproject.toml": toml('["ALL"]', "true") }, []), []);
  assert.ok(
    findings({ "pyproject.toml": toml('["E", "F"]', "true") }, []).some((e) =>
      /select exactly \["ALL"\]/.test(e),
    ),
  );
  assert.ok(
    findings({ "pyproject.toml": toml('["ALL"]', "false") }, []).some((e) =>
      /strict = true/.test(e),
    ),
  );
});
