import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { IntlMessageFormat } from "intl-messageformat";
import {
  addDomain,
  diagnosticRequirements,
  messageShape,
  pseudolocalize,
  readCatalog,
  validateCatalog,
  validateCoverage,
} from "./localization-policy.mjs";
import { renderedTokens, validateTokens } from "./localization-tokens.mjs";
const root = resolve(import.meta.dirname, "../src");
const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
const semantic = json("generated/convergence/semantic-core-v1.contract.json");
const requirements = diagnosticRequirements(
  semantic,
  json("generated/convergence/web-v2.host-failure.schema.json"),
);
const catalog = (language) =>
  Object.assign(
    {},
    ...readdirSync(resolve(root, "presentation/catalogs"))
      .filter((name) => name.startsWith(`${language}.`))
      .map((name) => json(`presentation/catalogs/${name}`)),
  );

test("every shipped real catalog covers the native metadata and diagnostic parameter vocabulary", () => {
  const en = catalog("en");
  assert.ok(Object.keys(en).length > 300);
  assert.ok(Object.keys(requirements).length > 100);
  for (const language of ["en", "lv", "ar"]) validateCatalog(en, catalog(language), language);
  validateCoverage(en, semantic, requirements);
  const tokens = renderedTokens(
    semantic,
    readFileSync(resolve(root, "generated/convergence/web-v2.types.recovery.ts"), "utf8"),
  );
  validateTokens(en, tokens);
});

test("catalog admission refuses duplicate JSON keys, non-object inputs and cross-domain ownership", () => {
  for (const value of ['{"a":"first","a":"second"}', "[]", "null", "42", "broken"])
    assert.throws(() => readCatalog(value));
  assert.deepEqual(readCatalog('{"ui.a":"safe"}'), { "ui.a": "safe" });
  assert.throws(() => addDomain({}, { "notice.a": "wrong domain" }, "ui"));
  assert.throws(() => addDomain({ "ui.a": "existing" }, { "ui.a": "duplicate" }, "ui"));
  const merged = {};
  addDomain(merged, { "ui.a": "safe" }, "ui");
  assert.deepEqual(merged, { "ui.a": "safe" });
});

test("catalog changes cannot silently lose keys, arguments or their closed roles", () => {
  const en = { "ui.notice": "Request {identity}" };
  for (const altered of [
    {},
    { "ui.notice": "Request", "ui.extra": "extra" },
    { "ui.notice": "Request {other}" },
    { "ui.notice": "{identity, plural, one {one} other {many}}" },
  ])
    assert.throws(() => validateCatalog(en, altered, "en"));
  validateCatalog(en, { "ui.notice": "Pieprasījums {identity}" }, "lv");
});

test("ICU admission rejects markup, raw value formatting and directional overrides", () => {
  for (const value of [
    "",
    "<b>{id}</b>",
    "text\u202E",
    "text\u2066",
    "{x, number}",
    "{x, date}",
    "{x, time}",
    "{x, number, ::currency/USD}",
    "{constructor}",
  ])
    assert.throws(() => messageShape(value, "en"), value);
});

test("plural grammar is locale-complete while exact selectors and offsets remain semantic", () => {
  const en = { "ui.count": "{n, plural, =2 {pair} one {one} other {#}}" };
  const ar = {
    "ui.count":
      "{n, plural, =2 {زوج} zero {صفر} one {واحد} two {اثنان} few {#} many {#} other {#}}",
  };
  validateCatalog(en, ar, "ar");
  assert.throws(() => validateCatalog(en, en, "ar"));
  assert.throws(() =>
    validateCatalog(
      en,
      { "ui.count": "{n, plural, offset:1 =2 {pair} one {one} other {#}}" },
      "en",
    ),
  );
  assert.throws(() =>
    validateCatalog(en, { "ui.count": "{n, plural, =3 {trio} one {one} other {#}}" }, "en"),
  );
  assert.throws(() => messageShape("{n} {n, plural, one {one} other {many}}", "en"));
});

test("pseudolocalization transforms only literal nodes and preserves exact interpolated identities", () => {
  const ast = messageShape(
    "Operation {id}: {n, plural, one {one result} other {# results}}",
    "en",
  ).ast;
  const before = structuredClone(ast);
  const changed = pseudolocalize(ast);
  assert.deepEqual(ast, before);
  const value = new IntlMessageFormat(changed, "en").format({ id: "UUID-12<exact>", n: 2 });
  assert.ok(value.includes("UUID-12<exact>"));
  assert.ok(value.includes("2"));
  assert.ok(!value.includes("Operation"));
});

test("missing or obsolete metadata, diagnostic parameters and rendered tokens fail closed", () => {
  const en = catalog("en");
  const missing = { ...en };
  delete missing[`field.${semantic.fields[0].name}.label`];
  assert.throws(() => validateCoverage(missing, semantic, requirements));
  assert.throws(() =>
    validateCoverage({ ...en, "diagnostic.OBSOLETE": "obsolete" }, semantic, requirements),
  );
  const id = Object.keys(requirements).find((key) => Object.keys(requirements[key]).length > 0);
  assert.throws(() =>
    validateCoverage({ ...en, [`diagnostic.${id}`]: "lost arguments" }, semantic, requirements),
  );
  assert.throws(() => validateTokens({ ...en, "token.UNREVIEWED": "unknown" }, []));
  assert.throws(() => renderedTokens({ fields: [], commands: [] }, "export type Missing = never;"));
});
