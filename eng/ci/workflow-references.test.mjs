import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { checkReferences, validateReferences } from "./workflow-references.mjs";

test("all literal workflow and plan entry points exist", () => {
  assert.deepEqual(checkReferences(fileURLToPath(new URL("../..", import.meta.url))), []);
});

test("deleted entry points and local actions are refused; comments and generated output are not executable references", () => {
  const source =
    "jobs:\n  probe:\n    uses: ./.github/workflows/child.yml\n  run:\n    steps:\n      - run: |\n          # node eng/comment.mjs\n          node eng/real.mjs artifacts/output.json\n      - uses: ./.github/actions/toolchain\n";
  const sources = new Map([[".github/workflows/check.yml", source]]);
  const files = [
    "eng/real.mjs",
    ".github/workflows/child.yml",
    ".github/actions/toolchain/action.yml",
  ];
  assert.deepEqual(validateReferences(sources, files), []);
  assert.equal(validateReferences(sources, []).length, 3);
});

test("security findings cannot be converted into a passing stage", () => {
  const plan = (/** @type {string[]} */ args) =>
    new Map([
      [
        "eng/ci/stage-plans/quality.json",
        JSON.stringify({ stages: [{ id: "workflow-security", argv: args }] }),
      ],
    ]);
  const args = ["uv", "run", "zizmor", "--persona", "pedantic"];
  assert.deepEqual(validateReferences(plan(args), []), []);
  assert.match(String(validateReferences(plan([...args, "--no-exit-codes"]), [])), /must fail/u);
  assert.match(String(validateReferences(plan(["uv", "run", "zizmor"]), [])), /pedantic/u);
});

test("quoted messages and inline comments cannot invent executed source references", () => {
  const source =
    "jobs:\n  probe:\n    steps:\n      - run: |\n          echo 'node eng/message.mjs; bash eng/text.sh'\n          node 'eng/real.mjs' # && node eng/comment.mjs\n";
  assert.deepEqual(
    validateReferences(new Map([[".github/workflows/check.yml", source]]), ["eng/real.mjs"]),
    [],
  );
});
