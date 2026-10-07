import assert from "node:assert/strict";
import test from "node:test";
import { scanFile } from "./scan.mjs";

/**
 * @param {string} path
 * @param {string} text
 * @returns {string[]} `tool:rule:kind:id` for every occurrence.
 */
function scan(path, text) {
  return scanFile({ path, text, lines: text.split(/\r?\n/u) }).map(
    (item) => `${item.tool}:${item.rule}:${item.kind}:${item.id ?? "-"}`,
  );
}

/** @type {Array<[string, string, string, string[]]>} */
const inlineCases = [
  [
    "F# nowarn",
    "src/A.fs",
    '#nowarn "3391" "25"\nlet x = 1\n',
    ["fsc:FS3391:inline:-", "fsc:FS25:inline:-"],
  ],
  [
    "F# pragma",
    "src/A.fs",
    "#pragma warning disable 3391 FS0025\n",
    ["fsc:FS3391:inline:-", "fsc:FS0025:inline:-"],
  ],
  [
    "fsharplint disable",
    "src/A.fs",
    "// fsharplint:disable-next-line RedundantNewKeyword\n",
    ["fsharplint:RedundantNewKeyword:inline:-"],
  ],
  ["fsharplint blanket disable", "src/A.fs", "// fsharplint:disable\n", ["fsharplint:*:inline:-"]],
  [
    "analyzer SuppressMessage",
    "src/A.fs",
    '[<SuppressMessage("Design", "CA1031")>]\nlet f () = ()\n',
    ["dotnet-analyzer:CA1031:inline:-"],
  ],
  [
    "oxlint disable",
    "web/src/a.ts",
    "// oxlint-disable-next-line no-console\n",
    ["oxlint:no-console:inline:-"],
  ],
  [
    "banned eslint disable",
    "web/src/a.ts",
    "/* eslint-disable no-console, no-debugger */\n",
    ["oxlint:no-console:inline:-", "oxlint:no-debugger:inline:-"],
  ],
  ["blanket oxlint disable", "web/src/a.ts", "/* oxlint-disable */\n", ["oxlint:*:inline:-"]],
  [
    "ts-ignore",
    "web/src/a.ts",
    "// @ts-ignore\n// @ts-expect-error reason\n// @ts-nocheck\n",
    ["tsc:@ts-ignore:inline:-", "tsc:@ts-expect-error:inline:-", "tsc:@ts-nocheck:inline:-"],
  ],
  [
    "prettier ignore",
    "web/src/a.ts",
    "// prettier-ignore\n",
    ["prettier:prettier-ignore:inline:-"],
  ],
  [
    "stylelint disable",
    "web/src/a.css",
    "/* stylelint-disable-next-line color-no-hex */\n",
    ["stylelint:color-no-hex:inline:-"],
  ],
  [
    "coverage ignore",
    "web/src/a.ts",
    "/* v8 ignore next */\n/* istanbul ignore if */\n",
    ["coverage:v8-ignore-next:inline:-", "coverage:istanbul-ignore-if:inline:-"],
  ],
  [
    "noqa with codes",
    "eng/x.py",
    "import os  # noqa: F401, E501\n",
    ["ruff:F401:inline:-", "ruff:E501:inline:-"],
  ],
  ["blanket noqa", "eng/x.py", "import os  # noqa\n", ["ruff:*:inline:-"]],
  ["ruff file noqa", "eng/x.py", "# ruff: noqa: S603\n", ["ruff:S603:inline:-"]],
  [
    "type ignore",
    "eng/x.py",
    "x: int = 'a'  # type: ignore[assignment]\n",
    ["mypy:assignment:inline:-"],
  ],
  ["format off", "eng/x.py", "# fmt: off\n", ["ruff:format-off:inline:-"]],
  [
    "pragma no cover",
    "eng/x.py",
    "def f():  # pragma: no cover\n    pass\n",
    ["coverage:pragma-no-cover:inline:-"],
  ],
  [
    "shellcheck disable",
    "eng/x.sh",
    "# shellcheck disable=SC2086,SC2046\n",
    ["shellcheck:SC2086:inline:-", "shellcheck:SC2046:inline:-"],
  ],
  [
    "yamllint disable",
    ".github/x.yml",
    "# yamllint disable-line rule:line-length\n",
    ["yamllint:rule:line-length:inline:-"],
  ],
];
for (const [name, path, text, expected] of inlineCases) {
  test(`scans ${name}`, () => assert.deepEqual(scan(path, text), expected));
}

test("a reference on the same line or the line above identifies the exception", () => {
  assert.deepEqual(
    scan("web/src/a.ts", "// oxlint-disable-next-line no-console -- lint-exception: LX-0007\n"),
    ["oxlint:no-console:inline:LX-0007"],
  );
  assert.deepEqual(
    scan("web/src/a.ts", "// lint-exception: LX-0008\n// oxlint-disable-next-line no-console\n"),
    ["oxlint:no-console:inline:LX-0008"],
  );
  assert.deepEqual(
    scan("web/src/a.ts", "// lint-exception: LX-0009\n\n// oxlint-disable-next-line no-console\n"),
    ["oxlint:no-console:inline:-"],
  );
});

test("text inside Python strings and docstrings is not a suppression", () => {
  const text = 'x = "# noqa"\n"""\n# type: ignore\n"""\ny = 1  # noqa: E501\n';
  assert.deepEqual(scan("eng/x.py", text), ["ruff:E501:inline:-"]);
});

/** @type {Array<[string, string, string, string[]]>} */
const configCases = [
  [
    "oxlint disabled rules and ignore patterns",
    "web/.oxlintrc.json",
    '{ // c\n "ignorePatterns": ["gen/*.mjs"], "rules": {"no-console": "off", "eqeqeq": "error"}, "overrides": [{"files": ["tests/**"], "rules": {"no-debugger": ["off"]}}] }',
    [
      "oxlint:no-console:config:-",
      'oxlint:override:["tests/**"]:[]:no-debugger:config:-',
      "oxlint:ignorePatterns:gen/*.mjs:config:-",
    ],
  ],
  [
    "tsconfig excludes",
    "web/tsconfig.app.json",
    '{ "exclude": ["src/legacy"] }',
    ["tsc:exclude:src/legacy:config:-"],
  ],
  [
    "knip ignores",
    "web/knip.json",
    '{ "ignore": ["a.ts"], "ignoreDependencies": ["b"] }',
    ["knip:ignore:a.ts:config:-", "knip:ignoreDependencies:b:config:-"],
  ],
  [
    "prettier ignore file",
    "web/.prettierignore",
    "# comment\nsrc/gen/*.json\n",
    ["prettier:ignore-file:src/gen/*.json:config:-"],
  ],
  [
    "stylelint disabled rules",
    "web/stylelint.config.mjs",
    'export default { rules: { "color-no-hex": null, "a": true }, ignoreFiles: [] };\n',
    ["stylelint:color-no-hex:config:-", "stylelint:ignore-options:config:-"],
  ],
  [
    "pyproject ignores",
    "pyproject.toml",
    '[tool.ruff.lint]\nignore = ["D100"]\n[tool.ruff.lint.per-file-ignores]\n"tests/*" = ["S101"]\n[tool.mypy]\nexclude = ["build"]\n[[tool.mypy.overrides]]\nmodule = ["x.*"]\nignore_errors = true\n',
    [
      "ruff:ignore:D100:config:-",
      "ruff:per-file-ignores:tests/*:S101:config:-",
      "mypy:exclude:build:config:-",
      "mypy:override:x.*:ignore_errors:config:-",
    ],
  ],
  [
    "zizmor audit configuration",
    ".github/zizmor.yml",
    "rules:\n  self-repository:\n    disable: true\n  template-injection:\n    ignore:\n      - ci.yml:20\n",
    [
      "zizmor:self-repository:disable:config:-",
      "zizmor:template-injection:ignore:ci.yml:20:config:-",
    ],
  ],
  [
    "shellcheck config",
    ".shellcheckrc",
    "disable=SC1091,SC2016\n",
    ["shellcheck:SC1091:config:-", "shellcheck:SC2016:config:-"],
  ],
  [
    "editorconfig silenced diagnostics",
    ".editorconfig",
    "[*.fs]\ndotnet_diagnostic.IDE0055.severity = none\ndotnet_diagnostic.CA1000.severity = error\n",
    ["dotnet-analyzer:IDE0055:config:-"],
  ],
  [
    "msbuild NoWarn",
    "src/A/A.fsproj",
    "<Project><PropertyGroup><NoWarn>$(NoWarn);3391;CS8600</NoWarn></PropertyGroup></Project>",
    ["msbuild:FS3391:config:-", "msbuild:CS8600:config:-"],
  ],
  [
    "coverage excludes",
    "web/vite.config.ts",
    "coverage: {\n  exclude: ['a'],\n}\n",
    ["coverage:coverage-config-exclude:config:-"],
  ],
];
for (const [name, path, text, expected] of configCases) {
  test(`scans ${name}`, () => assert.deepEqual(scan(path, text).sort(), [...expected].sort()));
}

test("suppression-like text in a script string or template literal is not a suppression", () => {
  const text =
    'const a = "// oxlint-disable-next-line no-console";\nconst b = `/* prettier-ignore */`;\n';
  assert.deepEqual(scan("web/src/a.ts", text), []);
});

test("a script that cannot be parsed is scanned line by line so nothing hides in it", () => {
  assert.deepEqual(scan("web/src/a.ts", "const = ;\n// oxlint-disable-next-line no-console\n"), [
    "oxlint:no-console:inline:-",
  ]);
});

test("a multi-line block comment is read whole", () => {
  assert.deepEqual(scan("web/src/a.ts", "/**\n * reason\n * oxlint-disable no-console\n */\n"), []);
  assert.deepEqual(scan("web/src/a.ts", "/* oxlint-disable no-console */\n"), [
    "oxlint:no-console:inline:-",
  ]);
});
