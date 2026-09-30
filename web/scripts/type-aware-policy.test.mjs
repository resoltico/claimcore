import assert from "node:assert/strict";
import test from "node:test";
import { lintCases } from "./lint-fixture.mjs";

const cases = [
  ["floating promises", "export function run() { Promise.resolve(1); }", "no-floating-promises"],
  [
    "unsafe any member access",
    "export function read(value: any) { return value.property; }",
    "no-unsafe-member-access",
  ],
  [
    "misused promise conditions",
    "export function check(p: Promise<boolean>) { if (p) return 1; return 0; }",
    "no-misused-promises",
  ],
  ["awaiting a non-promise", "export async function wait() { await 1; }", "await-thenable"],
];

for (const [name, code, rule] of cases) {
  test(`the type-aware gate rejects ${name}`, async () => {
    const [codes] = await lintCases([{ path: "src/module.ts", code: String(code) }], {
      typeAware: true,
    });
    assert.ok(
      codes?.some((found) => found.includes(String(rule))),
      `${rule} not reported: ${codes?.join(", ")}`,
    );
  });
}

test("the type-aware gate accepts handled promises and typed access", async () => {
  const code =
    "export async function run(value: { property: string }) { await Promise.resolve(1); return value.property; }";
  const [codes] = await lintCases([{ path: "src/module.ts", code }], { typeAware: true });
  assert.deepEqual(codes, []);
});
