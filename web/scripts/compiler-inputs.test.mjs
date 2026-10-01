import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { compilerInputs } from "./compiler-inputs.mjs";

test("asset identity includes compiler parents and refuses escaped or cyclic graphs", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-compiler-inputs-"));
  try {
    mkdirSync(join(root, "web"));
    writeFileSync(join(root, "strict.json"), '{"compilerOptions":{"strict":true}}\n');
    const leaf = join(root, "web/tsconfig.json");
    writeFileSync(leaf, '{// native JSONC inheritance\n"extends":"../strict.json"}\n');
    assert.deepEqual(compilerInputs(root, [leaf]), [join(root, "strict.json"), leaf].sort());
    writeFileSync(leaf, '{"extends":"../../outside.json"}\n');
    assert.throws(() => compilerInputs(root, [leaf]));
    writeFileSync(leaf, '{"extends":"./tsconfig.json"}\n');
    assert.throws(() => compilerInputs(root, [leaf]), /cycle/u);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
