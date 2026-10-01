import assert from "node:assert/strict";
import test from "node:test";
import { lintCases } from "./lint-fixture.mjs";

const networkCode = [
  'fetch("/api");',
  'window.fetch("/api");',
  'globalThis["fetch"]("/api");',
  'self.fetch("/api");',
  "const request = fetch;",
  "new XMLHttpRequest();",
  'new window.WebSocket("wss://localhost");',
  'new EventSource("/events");',
];
const localeCode = [
  'import { translate } from "../../presentation/messages";',
  'export { resolveLanguage } from "../presentation/preferences";',
  'void import("../presentation/context");',
  "new Intl.NumberFormat();",
  'window.localStorage.getItem("language");',
  "navigator.language;",
];
const boundaryRules =
  /no-restricted-globals|no-restricted-imports|no-presentation-import|no-danger/u;
const stateFiles = [
  "src/api/outcomes.ts",
  "src/domain/operationReducer.ts",
  "src/hooks/useOperation.ts",
  "src/views/recovery/RecoveryState.ts",
];

/** @param {string} path @param {string[]} codes @param {boolean} expectViolation */
async function assertEach(path, codes, expectViolation) {
  const found = await lintCases(codes.map((code) => ({ path, code })));
  codes.forEach((code, index) => {
    const rules = (found[index] ?? []).filter((rule) => boundaryRules.test(rule));
    assert.equal(rules.length > 0, expectViolation, `${path}: ${code} -> ${rules.join(", ")}`);
  });
}

for (const path of ["src/App.tsx", ...stateFiles]) {
  test(`browser networking is rejected in ${path}`, async () => {
    await assertEach(path, networkCode, true);
  });
}

test("only the validated API module may use browser networking", async () => {
  await assertEach("src/api/v3.ts", ['fetch("/api");', "new WebSocket('wss://localhost');"], false);
  await assertEach("src/api/outcomes.ts", ['fetch("/api");'], true);
});

for (const path of stateFiles) {
  test(`localization authority stays out of ${path}`, async () => {
    await assertEach(path, localeCode, true);
    await assertEach(path, ['import type { Notice } from "../api/notices";'], false);
  });
}

test("presentation code may use locale access and the validated API keeps its network ownership", async () => {
  await assertEach("src/presentation/format.ts", ["new Intl.NumberFormat();"], false);
  await assertEach("src/api/v3.ts", localeCode.slice(3), true);
});

test("ordinary local identifiers are not falsely rejected", async () => {
  await assertEach("src/App.tsx", ['function local(fetch) { return fetch("value"); }'], false);
  await assertEach("src/hooks/useOperation.ts", ["function local(Intl) { return Intl(); }"], false);
});

test("claimant content must render as text", async () => {
  await assertEach(
    "src/views/Notice.tsx",
    ["export const view = <div dangerouslySetInnerHTML={{ __html: 'x' }} />;"],
    true,
  );
});

test("Node tooling rejects browser globals while retaining Node runtime access", async () => {
  await assertEach("scripts/control.mjs", ["window.document.title;", "navigator.language;"], true);
  await assertEach("scripts/control.mjs", ["process.version;"], false);
});
