import assert from "node:assert/strict";
import test from "node:test";
import { Linter } from "eslint";
import globals from "globals";
import { browserNetworkBoundary } from "./architecture-policy.mjs";

function violations(code, filename) {
  return new Linter()
    .verify(
      code,
      [
        { files: ["src/**/*.{ts,tsx}"], languageOptions: { globals: globals.browser } },
        browserNetworkBoundary,
      ],
      { filename },
    )
    .filter((message) => message.ruleId === "no-restricted-globals");
}

for (const filename of ["src/App.tsx", "src/domain/metadata.ts", "src/hooks/useSession.ts"]) {
  test(`network boundary covers ${filename}`, () => {
    for (const code of [
      'fetch("/api");',
      'window.fetch("/api");',
      'globalThis["fetch"]("/api");',
      'self.fetch("/api");',
      "const request = fetch;",
      "new XMLHttpRequest();",
      'new window.WebSocket("wss://localhost");',
      'new EventSource("/events");',
    ]) {
      assert.ok(violations(code, filename).length > 0, code);
    }
  });
}

test("only the validated API module may use browser networking", () => {
  assert.equal(violations('fetch("/api");', "src/api/v2.ts").length, 0);
  assert.ok(violations('fetch("/api");', "src/api/outcomes.ts").length > 0);
});

test("ordinary local identifiers are not falsely rejected", () => {
  const code = 'function local(fetch) { return fetch("value"); }';
  assert.equal(violations(code, "src/App.tsx").length, 0);
});
