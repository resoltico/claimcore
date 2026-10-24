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

const repository = resolve(import.meta.dirname, "../..");
const locales = resolve(repository, "src/ClaimCore.Contract/Localization");
const contracts = resolve(repository, "contracts");
/** @param {string} path */
const json = (path) => JSON.parse(readFileSync(path, "utf8"));
const descriptors = json(resolve(contracts, "descriptors.json"));
const requirements = diagnosticRequirements(descriptors, json(resolve(contracts, "openapi.json")));
/** @param {string} language */
const catalog = (language) => {
  /** @type {Record<string, string>} */
  const result = {};
  for (const name of readdirSync(locales).filter((entry) => entry.startsWith(`${language}.`))) {
    addDomain(
      result,
      readCatalog(readFileSync(resolve(locales, name), "utf8")),
      name.slice(language.length + 1, -5),
    );
  }
  return result;
};

test("every authored language covers current metadata and public message families", () => {
  const english = catalog("en");
  assert.ok(Object.hasOwn(requirements, "fault.CORE_STORE_INTEGRITY_ERROR"));
  assert.ok(Object.hasOwn(requirements, "host.CONTRACT_MISMATCH"));
  for (const language of ["en", "lv", "ar"]) {
    validateCatalog(english, catalog(language), language);
    validateCoverage(catalog(language), descriptors, requirements);
  }
  validateTokens(
    english,
    renderedTokens(descriptors, readFileSync(resolve(contracts, "client.ts"), "utf8")),
  );
});

test("catalog admission rejects duplicates, nonstring messages and cross-domain ownership", () => {
  for (const source of ['{"ui.a":"first","ui.a":"second"}', "[]", "null", "42", "broken"]) {
    assert.throws(() => readCatalog(source));
  }
  for (const value of [42, null, {}, [], [{ literal: "old representation" }]]) {
    assert.throws(() => readCatalog(JSON.stringify({ "ui.invalid": value })));
  }
  assert.deepEqual(readCatalog('{"ui.a":"safe"}'), { "ui.a": "safe" });
  assert.throws(() => addDomain({}, { "notice.a": "wrong domain" }, "ui"));
  assert.throws(() => addDomain({ "ui.a": "first" }, { "ui.a": "second" }, "ui"));
});

test("the independent ICU vectors agree with the runtime formatter", () => {
  const vectors = json(resolve(repository, "tests/fixtures/message-template-vectors.json"));
  for (const row of vectors.accepted) {
    const shape = messageShape(row.source, "en");
    assert.deepEqual(Object.keys(shape.args).sort(), Object.keys(row.arguments).sort());
    assert.equal(new IntlMessageFormat(shape.ast, "en").format(row.arguments), row.expected);
  }
  for (const source of vectors.rejected) {
    assert.throws(() => messageShape(source, "en"));
  }
});

test("ICU admission enforces source bounds and scalar safety", () => {
  assert.doesNotThrow(() => messageShape("x".repeat(8000), "en"));
  for (const source of [
    "",
    " ",
    "x".repeat(8001),
    "\ud800",
    "\uffff",
    "\ufdd0",
    "\u{1ffff}",
    "hidden\u202etext",
    "<markup>",
    "control\u0001",
  ]) {
    assert.throws(() => messageShape(source, "en"));
  }
});

test("pseudolocalization preserves source ASTs and argument structure", () => {
  for (const source of Object.values(catalog("en"))) {
    const { ast, args } = messageShape(source, "en");
    const before = JSON.stringify(ast);
    const transformed = pseudolocalize(ast);
    assert.equal(JSON.stringify(ast), before);
    assert.notEqual(transformed, ast);
    const supplied = Object.fromEntries(
      Object.keys(args).map((name) => [name, "VALUE {literal}' العربية"]),
    );
    assert.doesNotThrow(() => new IntlMessageFormat(transformed, "en").format(supplied));
  }
});

test("metadata omissions, obsolete keys, wrong arguments and token drift fail", () => {
  const english = catalog("en");
  const missing = { ...english };
  delete missing[`field.${descriptors.fields[0].name}.label`];
  assert.throws(() => validateCoverage(missing, descriptors, requirements));
  assert.throws(() =>
    validateCoverage({ ...english, "diagnostic.OBSOLETE": "obsolete" }, descriptors, requirements),
  );
  const diagnosticKey = Object.keys(requirements).find(
    (key) => Object.keys(requirements[key] ?? {}).length > 0,
  );
  assert.ok(diagnosticKey);
  assert.throws(() =>
    validateCoverage({ ...english, [diagnosticKey]: "lost arguments" }, descriptors, requirements),
  );
  const tokens = renderedTokens(descriptors, readFileSync(resolve(contracts, "client.ts"), "utf8"));
  assert.throws(() => validateTokens({ ...english, "token.UNREVIEWED": "unknown" }, tokens));
  const missingScope = { ...english };
  delete missingScope["token.INSTALLATION"];
  assert.throws(() => validateTokens(missingScope, tokens));
});
