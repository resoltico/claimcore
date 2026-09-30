import assert from "node:assert/strict";
import test from "node:test";
import { Report } from "./model.mjs";
import { loadRegistry } from "./registry.mjs";
import { registryText, validEntry, withTree } from "./test-support.mjs";

/**
 * @param {Record<string, unknown>[]} exceptions
 * @param {Record<string, unknown>[]} [generated]
 * @returns {{ errors: string[], accepted: number }}
 */
function load(exceptions, generated = []) {
  return withTree(
    {
      "config/lint-exceptions.json": registryText(exceptions, generated),
      [String(validEntry.file)]: "",
    },
    (root) => {
      const report = new Report();
      const registry = loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
      return { errors: report.errors, accepted: registry.exceptions.length };
    },
  );
}

test("a complete entry is accepted", () => {
  assert.deepEqual(load([validEntry]), { errors: [], accepted: 1 });
});

/** @type {Array<[string, Record<string, unknown>, RegExp]>} */
const rejected = [
  ["a malformed id", { id: "LX-1" }, /id of the form LX-0000/],
  ["an unknown tool", { tool: "eslint" }, /unknown tool 'eslint'/],
  ["an unknown kind", { kind: "file" }, /kind must be inline or config/],
  ["a zero count", { count: 0 }, /positive integer count/],
  ["a fractional count", { count: 1.5 }, /positive integer count/],
  ["no rules", { rules: [] }, /exact rule names/],
  ["a blanket rule", { rules: ["*"] }, /blanket, wildcard or non-suppressible/],
  ["a wildcard inline rule", { rules: ["no-*"] }, /blanket, wildcard or non-suppressible/],
  ["a non-suppressible size rule", { rules: ["max-lines"] }, /non-suppressible/],
  ["a non-suppressible complexity rule", { rules: ["complexity"] }, /non-suppressible/],
  ["a wildcard file", { file: "web/src/*.ts" }, /one exact repository-relative file/],
  ["an absolute file", { file: "/etc/passwd" }, /one exact repository-relative file/],
  ["a missing file", { file: "web/src/missing.ts" }, /does not exist/],
  ["a short reason", { reason: "because" }, /substantive reason/],
  ["no owner", { owner: "" }, /named owner/],
  ["no review or expiry date", { reviewOn: undefined }, /requires reviewOn or expiresOn/],
  ["a malformed date", { reviewOn: "next year" }, /ISO yyyy-MM-dd/],
  ["an expired review", { reviewOn: "2000-01-01" }, /passed its reviewOn date/],
  [
    "an expired exception",
    { reviewOn: undefined, expiresOn: "2000-01-01" },
    /passed its expiresOn date/,
  ],
];
for (const [name, change, message] of rejected) {
  test(`the registry rejects ${name}`, () => {
    const { errors, accepted } = load([{ ...validEntry, ...change }]);
    assert.ok(
      errors.some((error) => message.test(error)),
      errors.join("\n"),
    );
    assert.equal(accepted, 0);
  });
}

test("the registry rejects duplicate ids", () => {
  assert.ok(
    load([validEntry, validEntry]).errors.some((error) => /duplicates another id/.test(error)),
  );
});

test("config exceptions may name pattern targets but never a blanket rule", () => {
  const pattern = { ...validEntry, kind: "config", rules: ["ignorePatterns:src/generated/*.mjs"] };
  assert.deepEqual(load([pattern]).errors, []);
  assert.ok(load([{ ...pattern, rules: ["*"] }]).errors.length > 0);
});

test("only recognized generated-output paths may be excluded", () => {
  const generated = {
    path: "web/dist/",
    generator: "the Vite production builder",
    reason: "Generated output that is never hand-authored.",
    owner: "project maintainers",
    reviewOn: "2999-01-01",
  };
  assert.deepEqual(load([], [generated]).errors, []);
  assert.ok(
    load([], [{ ...generated, path: "web/src/" }]).errors.some((error) =>
      /recognized generated-output path/.test(error),
    ),
  );
  assert.ok(
    load([], [{ ...generated, generator: "tool" }]).errors.some((error) =>
      /generator specifically/.test(error),
    ),
  );
  assert.ok(load([], [generated, generated]).errors.some((error) => /listed twice/.test(error)));
});

test("an unreadable or wrong-version registry is reported, not ignored", () => {
  withTree({ "config/lint-exceptions.json": "{" }, (root) => {
    const report = new Report();
    loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
    assert.match(report.errors.join("\n"), /cannot be read/);
  });
  withTree({ "config/lint-exceptions.json": JSON.stringify({ version: 1 }) }, (root) => {
    const report = new Report();
    loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
    assert.match(report.errors.join("\n"), /version 2/);
  });
});
