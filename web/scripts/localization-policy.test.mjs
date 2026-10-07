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
import { businessMessages } from "./localization-metadata.mjs";
import { renderedTokens, validateTokens } from "./localization-tokens.mjs";
const root = resolve(import.meta.dirname, "../src");
/** @param {string} path */
const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
const semantic = json("generated/contracts/semantic-core-v1.contract.json");
const requirements = diagnosticRequirements(
  semantic,
  json("generated/contracts/web-v3.host-failure.schema.json"),
);
/** @param {string} language @returns {Record<string, import("./localization-policy.mjs").Message>} */
const catalog = (language) =>
  Object.assign(
    language === "en"
      ? {
          ...businessMessages(semantic),
          ...json("generated/contracts/default-presentation.en.json"),
        }
      : {},
    ...readdirSync(resolve(root, "presentation/catalogs"))
      .filter((name) => name.startsWith(`${language}.`))
      .map((name) => json(`presentation/catalogs/${name}`)),
  );

test("every shipped real catalog covers the native metadata and diagnostic parameter vocabulary", () => {
  const en = catalog("en");
  assert.ok(Object.keys(en).length > 300);
  assert.ok(Object.keys(requirements).length > 100);
  for (const language of ["en", "lv", "ar"]) {
    validateCatalog(en, catalog(language), language);
  }
  validateCoverage(en, semantic, requirements);
  const tokens = renderedTokens(
    semantic,
    readFileSync(resolve(root, "generated/contracts/web-v3.types.recovery.ts"), "utf8"),
  );
  validateTokens(en, tokens);
});

test("catalog admission refuses duplicate JSON keys, non-object inputs and cross-domain ownership", () => {
  for (const value of ['{"a":"first","a":"second"}', "[]", "null", "42", "broken"]) {
    assert.throws(() => readCatalog(value));
  }
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
  ]) {
    assert.throws(() => validateCatalog(en, altered, "en"));
  }
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
  ]) {
    assert.throws(() => messageShape(value, "en"), value);
  }
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
  const { ast } = messageShape(
    "Operation {id}: {n, plural, one {one result} other {# results}}",
    "en",
  );
  const before = structuredClone(ast);
  const changed = pseudolocalize(ast);
  assert.deepEqual(ast, before);
  const value = new IntlMessageFormat(changed, "en").format({ id: "UUID-12<exact>", n: 2 });
  assert.ok(typeof value === "string");
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
  const id = Object.keys(requirements).find(
    (key) => Object.keys(requirements[key] ?? {}).length > 0,
  );
  assert.throws(() =>
    validateCoverage({ ...en, [`diagnostic.${id}`]: "lost arguments" }, semantic, requirements),
  );
  assert.throws(() => validateTokens({ ...en, "token.UNREVIEWED": "unknown" }, []));
  assert.throws(() => renderedTokens({ fields: [], commands: [] }, "export type Missing = never;"));
});

test("default business projection preserves literal apostrophes and braces without admitting interpolation", () => {
  for (const text of [
    "Handler's claim",
    "Use {literal} and {}",
    "'{name}'",
    "A }{ B",
    "{id, plural, other {unsafe}}",
  ]) {
    const { ast, args } = messageShape([{ literal: text }], "en");
    assert.deepEqual(args, {});
    assert.equal(new IntlMessageFormat(ast, "en").format(), text);
  }
  const descriptors = {
    fields: [
      { name: "insurerName", label: "Responsible insurer", meaning: "Recorded insurer {role}." },
    ],
    commands: [
      {
        kind: "OPEN",
        label: "Register case",
        meaning: "Register the case.",
        inputs: { groups: [] },
      },
    ],
  };
  assert.deepEqual(businessMessages(descriptors), {
    "field.insurerName.label": [{ literal: "Responsible insurer" }],
    "field.insurerName.meaning": [{ literal: "Recorded insurer {role}." }],
    "command.OPEN.label": [{ literal: "Register case" }],
    "command.OPEN.meaning": [{ literal: "Register the case." }],
  });
  assert.throws(() =>
    businessMessages({ ...descriptors, fields: [...descriptors.fields, ...descriptors.fields] }),
  );
});

// Independent oracle: original literals plus supplied hole values, never encoded text.
test("typed literals round-trip exhaustive syntax interactions and seeded Unicode at hole boundaries", () => {
  const alphabet = ["{", "}", "'", "a"];
  let cases = [""];
  /** @param {string} text */
  const verify = (text) => {
    const parts = [{ literal: text }, { hole: "identity" }, { literal: text }];
    const { ast, args } = messageShape(parts, "en");
    assert.deepEqual(args, { identity: "string" });
    const identity = "VALUE {notAnArgument}' العربية";
    assert.equal(new IntlMessageFormat(ast, "en").format({ identity }), text + identity + text);
    if (text.trim()) {
      const literal = messageShape([{ literal: text }], "en");
      assert.deepEqual(literal.args, {});
      assert.equal(new IntlMessageFormat(literal.ast, "en").format(), text);
    }
  };
  for (let length = 0; length <= 6; length++) {
    cases.forEach(verify);
    cases = cases.flatMap((prefix) => alphabet.map((char) => prefix + char));
  }
  let seed = 20261006;
  const unicode = [...alphabet, "é", "م", "😀", "\u200d", "\u0301", " "];
  for (let sample = 0; sample < 200; sample++) {
    let text = "";
    for (let index = 0; index < 80; index++) {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
      text += unicode[seed % unicode.length];
    }
    verify(text);
  }
  /** @param {string} text */
  const old = (text) => text.replaceAll("'", "''").replace(/[{}]+/gu, (braces) => `'${braces}'`);
  for (const text of ["{'{", "{'}", "}'{", "}'}"]) {
    assert.notEqual(new IntlMessageFormat(old(text), "en").format(), text);
    verify(text);
  }
});

test("typed-part admission retains bounds, unsafe-literal and exact argument roles", () => {
  for (const parts of [
    [],
    [{ literal: "" }],
    [{ literal: " " }],
    [{ literal: "x".repeat(8001) }],
    [{ literal: "<b>" }],
    [{ literal: "\u202e" }],
    [{ hole: "constructor" }],
    [{ hole: "not-a-name" }],
    [{ literal: "a", hole: "identity" }],
  ]) {
    assert.throws(() => messageShape(parts, "en"));
  }
  assert.throws(() => validateCatalog({ a: [{ hole: "identity" }] }, { a: "{other}" }, "en"));
  validateCatalog({ a: [{ literal: "" }, { hole: "identity" }] }, { a: "{identity}" }, "en");
});
