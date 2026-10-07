import assert from "node:assert/strict";
import test from "node:test";
import { findings, validEntry } from "./test-support.mjs";
import { scanFile } from "./scan.mjs";

/** @param {string} path @param {string} text */
const scan = (path, text) => scanFile({ path, text, lines: text.split("\n") });

test("equal aggregate counts cannot conceal a stale listed rule", () => {
  const source = "// lint-exception: LX-0001\n// oxlint-disable no-console\n";
  const errors = findings({ [validEntry.file]: source.repeat(2) }, [
    { ...validEntry, rules: ["no-console", "no-debugger"], count: 2 },
  ]);
  assert.match(errors.join("\n"), /no-debugger.*found 0/u);
});

test("override exception identity includes every admitted and excluded selector", () => {
  const path = "eng/.oxlintrc.json";
  const config = (/** @type {string[]} */ files, /** @type {string[]} */ excludeFiles = []) =>
    JSON.stringify({ overrides: [{ files, excludeFiles, rules: { "require-await": "off" } }] });
  const rule = scan(path, config(["tests/**"]))[0]?.rule;
  assert.equal(rule, 'override:["tests/**"]:[]:require-await');
  const entry = { ...validEntry, file: path, kind: "config", rules: [rule] };
  for (const source of [config(["**/*"]), config(["tests/**"], ["tests/important.mjs"])]) {
    assert.match(findings({ [path]: source }, [entry]).join("\n"), /unregistered oxlint/u);
  }
  assert.throws(() => scan(path, '{"overrides":[{"rules":{"no-console":"off"}}]}'));
});

test("two identifiers cannot claim the same configuration target", () => {
  const file = "web/.prettierignore";
  const entry = { ...validEntry, tool: "prettier", kind: "config", file, rules: ["ignore-file:x"] };
  const errors = findings({ [file]: "x\n" }, [entry, { ...entry, id: "LX-0002" }]);
  assert.match(errors.join("\n"), /duplicates an existing config exception target/u);
});

test("multiline JavaScript and CSS disable directives retain exact rules", () => {
  for (const [path, tool] of [
    ["web/src/a.ts", "oxlint"],
    ["web/src/a.css", "stylelint"],
  ]) {
    const found = scan(path ?? "", `/* ${tool}-disable\n no-console,\n no-debugger\n */\n`);
    assert.deepEqual(
      found.map(({ rule }) => rule),
      ["no-console", "no-debugger"],
    );
  }
});

test("real checker directive aliases cannot hide behind unsupported spellings", () => {
  assert.deepEqual(
    scan(
      ".github/workflows/a.yml",
      "# zizmor: ignore[template-injection, dangerous-triggers]\n",
    ).map(({ tool, rule }) => `${tool}:${rule}`),
    ["zizmor:template-injection", "zizmor:dangerous-triggers"],
  );
  assert.equal(scan("eng/a.sh", "# shellcheck disable=SC1000-SC9999\n")[0]?.rule, "SC1000-SC9999");
  const found = scan(
    "pyproject.toml",
    '[tool.mypy]\nexclude = "eng/.*"\nignore_errors = true\nfollow_imports = "skip"\n',
  );
  assert.deepEqual(
    found.map(({ rule }) => rule),
    ["exclude:eng/.*", "ignore_errors", "follow_imports:skip"],
  );
});

test("whole-file and all-diagnostics escapes cannot be registered", () => {
  for (const [tool, rule] of [
    ["shellcheck", "all"],
    ["shellcheck", "SC1000-SC9999"],
    ["tsc", "@ts-nocheck"],
    ["mypy", "ignore_errors"],
  ]) {
    const errors = findings({ [validEntry.file]: "" }, [{ ...validEntry, tool, rules: [rule] }]);
    assert.match(errors.join("\n"), /blanket, wildcard or non-suppressible/u);
  }
});

test("per-rule multiplicity cannot be redistributed under an unchanged aggregate count", () => {
  const source = (/** @type {string} */ rule) =>
    `// lint-exception: LX-0001\n// oxlint-disable ${rule}\n`;
  const errors = findings(
    { [validEntry.file]: source("no-console").repeat(3) + source("no-debugger") },
    [{ ...validEntry, rules: ["no-console", "no-debugger"], count: 4 }],
  );
  assert.match(errors.join("\n"), /each listed rule exactly once/u);
  const changed = findings({ [validEntry.file]: source("no-console").repeat(2) }, [
    { ...validEntry, rules: ["no-console", "no-debugger"], count: 2 },
  ]);
  assert.match(changed.join("\n"), /no-console.*found 2/u);
});

test("references in JavaScript data cannot impersonate suppression comments", () => {
  for (const text of [
    'const data = "lint-exception: LX-0001"; // oxlint-disable-next-line no-console\nconsole.log(1);\n',
    'const data = "lint-exception: LX-0001";\n// oxlint-disable-next-line no-console\nconsole.log(1);\n',
    "const data = `\n// lint-exception: LX-0001\n`;\n// oxlint-disable-next-line no-console\nconsole.log(1);\n",
  ]) {
    assert.equal(scan(validEntry.file, text)[0]?.id, null);
  }
});

test("native compiler pragma and project flags expose exact or blanket exclusions", () => {
  const text =
    '#pragma clang diagnostic ignored "-Wunused-variable"\n#pragma GCC diagnostic ignored "-Wconversion"\n_Pragma("clang diagnostic ignored \\"-Wunused-function\\\"")\n';
  assert.deepEqual(
    scan("src/native.c", text).map(({ rule }) => rule),
    ["-Wunused-variable", "-Wconversion", "-Wunused-function"],
  );
  assert.equal(scan("src/native.c", "_Pragma(WARNING_POLICY)\n")[0]?.rule, "dynamic-pragma");
  for (const project of [
    "<Project><PropertyGroup><NoWarn>$(InjectedWarnings)</NoWarn></PropertyGroup></Project>",
    "<Project><PropertyGroup><OtherFlags>--warn:0</OtherFlags></PropertyGroup></Project>",
    "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>",
    '<Project><Target><Exec Command="cc -Wno-error input.c" /></Target></Project>',
  ]) {
    assert.ok(scan("src/A.targets", project).some(({ rule }) => rule === "*"));
  }
  assert.deepEqual(
    scan(
      "src/A.targets",
      '<Project><OtherFlags>--nowarn:3391,25</OtherFlags><Exec Command="cc -Wno-unused-function a.c" /></Project>',
    ).map(({ rule }) => rule),
    ["FS3391", "FS25", "-Wunused-function"],
  );
});

test("per-module import skipping and TypeScript compiler escape flags are registered inputs", () => {
  const found = scan(
    "pyproject.toml",
    '[[tool.mypy.overrides]]\nmodule = ["critical"]\nfollow_imports = "skip"\n',
  );
  assert.equal(found[0]?.rule, "override:critical:follow_imports:skip");
  for (const key of ["noCheck", "skipLibCheck"]) {
    assert.equal(
      scan("web/tsconfig.json", JSON.stringify({ compilerOptions: { [key]: true } }))[0]?.rule,
      key,
    );
  }
});

test("blanket config selectors, diagnostic families and continued native pragmas refuse", () => {
  for (const [tool, rule] of [
    ["ruff", "ignore:ALL"],
    ["ruff", "ignore:C"],
    ["ruff", "ignore:PLR091"],
    ["oxlint", "ignorePatterns:**/*"],
  ]) {
    const errors = findings({ [validEntry.file]: "" }, [
      { ...validEntry, kind: "config", tool, rules: [rule] },
    ]);
    assert.match(errors.join("\n"), /blanket, wildcard or non-suppressible/u);
  }
  const source = '#pragma GCC diagnostic \\\n ignored "-Wunused-variable"\n';
  assert.ok(scan("src/native.c", source).some(({ rule }) => rule === "*"));
});

test("non-script reference lookup cannot borrow an identifier from source data", () => {
  for (const [path, source] of [
    ["eng/a.py", 'marker = "lint-exception: LX-0001"\nvalue = 1  # noqa: F401\n'],
    ["eng/a.py", 'marker = "lint-exception: LX-0001"  # noqa: F401\n'],
    [
      "src/A.fs",
      'let marker = "lint-exception: LX-0001"\n// fsharplint:disable-next-line RedundantNewKeyword\n',
    ],
    [
      "src/A.fs",
      '[<SuppressMessage("Design", "CA1031", Justification="lint-exception: LX-0001")>]\n',
    ],
    ["eng/a.py", '"""data\n# lint-exception: LX-0001"""\nvalue = 1  # noqa: F401\n'],
  ]) {
    assert.equal(scan(path ?? "", source ?? "")[0]?.id, null);
  }
});

test("triple quotes in a Python comment cannot hide later real suppressions", () => {
  const found = scan("eng/a.py", '# explanatory triple quote: """\nimport os  # noqa: F401\n');
  assert.equal(found[0]?.rule, "F401");
});
