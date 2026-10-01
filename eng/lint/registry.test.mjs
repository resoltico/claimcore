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
  ["a malformed id", { id: "LX-1" }, /id of the form LX-0000/u],
  ["an unknown tool", { tool: "eslint" }, /unknown tool 'eslint'/u],
  ["an unknown kind", { kind: "file" }, /kind must be inline or config/u],
  ["a zero count", { count: 0 }, /positive integer count/u],
  ["a fractional count", { count: 1.5 }, /positive integer count/u],
  ["no rules", { rules: [] }, /exact rule names/u],
  ["a blanket rule", { rules: ["*"] }, /blanket, wildcard or non-suppressible/u],
  ["a wildcard inline rule", { rules: ["no-*"] }, /blanket, wildcard or non-suppressible/u],
  ["a non-suppressible size rule", { rules: ["max-lines"] }, /non-suppressible/u],
  ["a non-suppressible complexity rule", { rules: ["complexity"] }, /non-suppressible/u],
  ["a wildcard file", { file: "web/src/*.ts" }, /one exact repository-relative file/u],
  ["an absolute file", { file: "/etc/passwd" }, /one exact repository-relative file/u],
  ["a missing file", { file: "web/src/missing.ts" }, /does not exist/u],
  ["a short reason", { reason: "because" }, /substantive reason/u],
  ["no owner", { owner: "" }, /named owner/u],
  ["no review or expiry date", { reviewOn: undefined }, /requires reviewOn or expiresOn/u],
  ["an impossible calendar day", { reviewOn: "2999-02-31" }, /ISO yyyy-MM-dd/u],
  ["a non-leap day", { reviewOn: "2999-02-29" }, /ISO yyyy-MM-dd/u],
  ["a parent path escape", { file: "../outside" }, /one exact repository-relative file/u],
  ["duplicate rules", { rules: ["no-console", "no-console"] }, /exact rule names/u],
  ["unknown fields", { extraPolicy: true }, /unknown fields/u],
  ["a malformed optional expiry", { expiresOn: 29990101 }, /ISO yyyy-MM-dd/u],
  ["a null review date", { reviewOn: null }, /ISO yyyy-MM-dd/u],
  ["a malformed date", { reviewOn: "next year" }, /ISO yyyy-MM-dd/u],
  ["an expired review", { reviewOn: "2000-01-01" }, /passed its reviewOn date/u],
  [
    "an expired exception",
    { reviewOn: undefined, expiresOn: "2000-01-01" },
    /passed its expiresOn date/u,
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
    load([validEntry, validEntry]).errors.some((error) => /duplicates another id/u.test(error)),
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
      /recognized generated-output path/u.test(error),
    ),
  );
  assert.ok(
    load([], [{ ...generated, generator: "tool" }]).errors.some((error) =>
      /generator specifically/u.test(error),
    ),
  );
  assert.ok(load([], [generated, generated]).errors.some((error) => /listed twice/u.test(error)));
});

test("an unreadable or wrong-version registry is reported, not ignored", () => {
  withTree({ "config/lint-exceptions.json": "{" }, (root) => {
    const report = new Report();
    loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
    assert.match(report.errors.join("\n"), /cannot be read/u);
  });
  withTree({ "config/lint-exceptions.json": JSON.stringify({ version: 1 }) }, (root) => {
    const report = new Report();
    loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
    assert.match(report.errors.join("\n"), /version 2/u);
  });
});

test("valid leap days are accepted and malformed registry members cannot disappear", () => {
  assert.deepEqual(load([{ ...validEntry, reviewOn: "2996-02-29" }]).errors, []);
  for (const malformed of [
    { version: 2, generated: [], exceptions: [null] },
    { version: 2, generated: [false], exceptions: [] },
    { version: 2, generated: [], exceptions: [], typo: true },
  ]) {
    withTree({ "config/lint-exceptions.json": JSON.stringify(malformed) }, (root) => {
      const report = new Report();
      const registry = loadRegistry(root, `${root}/config/lint-exceptions.json`, report);
      assert.match(report.errors.join("\n"), /complete object arrays/u);
      assert.equal(registry.exceptions.length, 0);
    });
  }
});

test("review and expiry dates remain valid through their entire UTC day", (context) => {
  context.mock.timers.enable({ apis: ["Date"], now: new Date("2040-02-29T23:59:59.999Z") });
  const entry = { ...validEntry, reviewOn: "2040-02-29", expiresOn: "2040-02-29" };
  assert.deepEqual(load([entry]).errors, []);
  context.mock.timers.setTime(new Date("2040-03-01T00:00:00Z").getTime());
  assert.equal(load([entry]).errors.filter((error) => /passed its/u.test(error)).length, 2);
});
