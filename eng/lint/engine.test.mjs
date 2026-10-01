import assert from "node:assert/strict";
import test from "node:test";
import { checkRepository } from "./engine.mjs";
import { findings, registryText, validEntry, withTree } from "./test-support.mjs";

const source = "web/src/example.ts";
const suppressed =
  "// lint-exception: LX-0001\n// oxlint-disable-next-line no-console\nconsole.log(1);\n";

test("a registered inline suppression passes and every entry is accounted for", () => {
  assert.deepEqual(findings({ [source]: suppressed }, [validEntry]), []);
});

test("an inline suppression without a reference fails", () => {
  const errors = findings(
    { [source]: "// oxlint-disable-next-line no-console\nconsole.log(1);\n" },
    [validEntry],
  );
  assert.ok(
    errors.some((error) => /needs a nearby 'lint-exception: LX-0000' reference/u.test(error)),
  );
  assert.ok(errors.some((error) => /Stale exception LX-0001/u.test(error)));
});

test("a blanket suppression fails even with a reference", () => {
  const errors = findings({ [source]: "// lint-exception: LX-0001\n/* oxlint-disable */\n" }, [
    validEntry,
  ]);
  assert.ok(errors.some((error) => /blanket suppression/u.test(error)));
});

/** @type {Array<[string, Record<string, unknown>, RegExp]>} */
const mismatches = [
  ["another file", { file: "web/src/other.ts" }, /is registered for web\/src\/other\.ts/u],
  ["another tool", { tool: "tsc", rules: ["@ts-ignore"] }, /is registered for tsc/u],
  ["a rule it does not list", { rules: ["no-debugger"] }, /does not list no-console/u],
  ["a config kind", { kind: "config" }, /is not an inline exception/u],
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
  assert.ok(errors.some((error) => /unknown exception LX-0042/u.test(error)));
});

test("an entry with no suppression left is stale", () => {
  const errors = findings({ [source]: "export const value = 1;\n" }, [validEntry]);
  assert.ok(errors.some((error) => /Stale exception LX-0001/u.test(error)));
});

test("the entry count must equal the occurrences", () => {
  const twice = `${suppressed}${suppressed}`;
  assert.ok(
    findings({ [source]: twice }, [validEntry]).some((error) =>
      /covers 1 occurrence\(s\) but .* has 2/u.test(error),
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
    findings(files, entries).filter((error) => !/must set/u.test(error));
  assert.deepEqual(exceptionFindings([config]), []);
  assert.ok(
    exceptionFindings([]).some((error) =>
      /unregistered oxlint exception: ignorePatterns:gen\/\*\.mjs/u.test(error),
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
    /cannot be read/u,
  );
});

/** @type {Array<[string, string, string, RegExp]>} */
const policies = [
  [
    "a focused F# test",
    "tests/A.Tests/T.fs",
    'let t = ftestCase "x" ignore\n',
    /focused or skipped F# test/u,
  ],
  [
    "structural F# test formatting",
    "tests/A.Tests/T.fs",
    'let t = sprintf "%A" 1\n',
    /structural formatting/u,
  ],
  ["a focused web test", "web/tests/a.test.ts", "it.only('x', () => {});\n", /focused, skipped/u],
  [
    "a web test annotation",
    "web/e2e/a.spec.ts",
    "test.info().annotations.push({});\n",
    /test annotation/u,
  ],
  [
    "a web retry",
    "web/tests/a.test.ts",
    "test.describe.configure({ retries: 2 });\n",
    /retry or expected-failure/u,
  ],
  [
    "a skipped Python test",
    "eng/Test-X.py",
    "@unittest.skip('x')\n",
    /skipped or expected-failure Python/u,
  ],
  [
    "a CI test filter",
    ".github/workflows/x.yml",
    "run: dotnet test --filter Name=x\n",
    /required-CI test filter/u,
  ],
  [
    "a stage-plan test filter",
    "eng/ci/stage-plans/quality.json",
    '{ "argv": ["x", "--grep", "y"] }\n',
    /required-CI test filter/u,
  ],
  [
    "a test-selection escape",
    "web/package.json",
    '{ "scripts": { "t": "vitest --passWithNoTests" } }\n',
    /test-selection escape/u,
  ],
  [
    "Playwright retries",
    "web/playwright.config.ts",
    "export default { retries: 2 };\n",
    /zero-retry/u,
  ],
  [
    "Vitest retries",
    "web/vite.config.ts",
    "export default { test: { retry: 1 } };\n",
    /zero-retry/u,
  ],
  [
    "a file over the size limit",
    "web/src/big.ts",
    `${"const a = 1;\n".repeat(301)}`,
    /301 physical lines/u,
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
    }).some((e) => /weakens 'cyclomaticComplexity' to 30/u.test(e)),
  );
  assert.ok(
    errors({ ...good, cyclomaticComplexity: { enabled: false } }).some((e) =>
      /disables 'cyclomaticComplexity'/u.test(e),
    ),
  );
  assert.ok(
    errors({ ...good, ignoreFiles: ["src/x.fs"] }).some((e) =>
      /ignoreFiles must exactly match/u.test(e),
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
      categories: Object.fromEntries(
        ["correctness", "suspicious", "pedantic", "perf", "style"].map((name) => [name, "error"]),
      ),
      options: { denyWarnings: true, reportUnusedDisableDirectives: "error" },
      rules: {
        complexity: ["error", { max: complexity }],
        "max-lines": ["error", { max: 300 }],
        "max-lines-per-function": ["error", { max: 50, IIFEs: true }],
        "max-params": ["error", { max: 5 }],
        "max-statements": ["error", { max: 50 }],
      },
    });
  assert.deepEqual(
    findings({ "web/.oxlintrc.json": config(12) }, []).filter((e) => /must (set|enable)/u.test(e)),
    [],
  );
  assert.ok(
    findings({ "web/.oxlintrc.json": config(13) }, []).some((e) =>
      /must set 'complexity'/u.test(e),
    ),
  );
  assert.ok(
    findings({ "web/.oxlintrc.json": "{}" }, []).some((e) => /must set 'max-lines'/u.test(e)),
  );
});

test("oxlint must keep every rule category at error and deny warnings", () => {
  const errors = findings(
    { "eng/.oxlintrc.json": JSON.stringify({ categories: { perf: "warn" } }) },
    [],
  );
  assert.ok(errors.some((e) => /'correctness' rule category/u.test(e)));
  assert.ok(errors.some((e) => /'perf' rule category/u.test(e)));
  assert.ok(errors.some((e) => /denyWarnings/u.test(e)));
});
