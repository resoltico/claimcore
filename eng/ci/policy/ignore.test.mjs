import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { checkIgnorePolicy } from "./ignore.mjs";

/**
 * @param {Record<string, string>} files
 * @param {(root: string) => void} body
 */
function withRepository(files, body) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-ignore-fixture-"));
  try {
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const policy = {
  mustBeIgnored: ["secret.key", "out/report.json"],
  mustRemainVisible: ["src/main.fs"],
};
const files = { ".gitignore": "*.key\nout/\n", "src/main.fs": "x" };

test("the policy passes when private paths are ignored and public inputs are visible", () => {
  withRepository(files, (root) => {
    assert.match(checkIgnorePolicy(root, policy), /2 private\/generated probes and 1 public/u);
  });
});

test("a private path that is not ignored is refused", () => {
  withRepository({ ...files, ".gitignore": "out/\n" }, (root) => {
    assert.throws(() => checkIgnorePolicy(root, policy), /not ignored: secret\.key/u);
  });
});

test("a public input that is ignored or missing is refused", () => {
  withRepository({ ...files, ".gitignore": "*.key\nout/\n*.fs\n" }, (root) => {
    assert.throws(() => checkIgnorePolicy(root, policy), /is ignored: src\/main\.fs/u);
  });
  withRepository({ ".gitignore": "*.key\nout/\n" }, (root) => {
    assert.throws(() => checkIgnorePolicy(root, policy), /is missing: src\/main\.fs/u);
  });
});
