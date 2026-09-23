import assert from "node:assert/strict";
import test from "node:test";
import { Linter } from "eslint";
import tseslint from "typescript-eslint";
import globals from "globals";
import { dynamicLocaleImport, localeStateBoundaries } from "./localization-boundary.mjs";
import { browserNetworkBoundary } from "./architecture-policy.mjs";
const violations = (code, filename) =>
  new Linter().verify(
    code,
    [
      {
        files: ["**/*.{ts,tsx}"],
        languageOptions: { parser: tseslint.parser, globals: globals.browser },
      },
      browserNetworkBoundary,
      ...localeStateBoundaries.map((policy) => ({
        ...policy,
        rules: { ...policy.rules, "no-restricted-syntax": ["error", dynamicLocaleImport] },
      })),
    ],
    { filename },
  );
for (const filename of [
  "src/api/v2.ts",
  "src/domain/operationReducer.ts",
  "src/hooks/useOperation.ts",
  "src/views/recovery/RecoveryState.ts",
]) {
  test(`localization authority stays out of ${filename}`, () => {
    for (const code of [
      'import { translate } from "../../presentation/messages";',
      'export { resolveLanguage } from "../presentation/preferences";',
      'void import("../presentation/context");',
      "new Intl.NumberFormat();",
      'window.localStorage.getItem("language");',
      "navigator.language;",
    ])
      assert.ok(violations(code, filename).length > 0, code);
    assert.equal(violations('import type { Notice } from "../api/notices";', filename).length, 0);
  });
}
test("localization guard retains network ownership and permits presentation-only locale access", () => {
  assert.ok(violations('fetch("/api");', "src/hooks/useOperation.ts").length > 0);
  assert.equal(violations('fetch("/api");', "src/api/v2.ts").length, 0);
  assert.equal(violations("new Intl.NumberFormat();", "src/presentation/format.ts").length, 0);
  assert.equal(
    violations("function local(Intl) { return Intl(); }", "src/hooks/useOperation.ts").length,
    0,
  );
});
