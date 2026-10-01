// A throwaway repository tree for tests: every case builds its own, so no test reads or writes the
// real checkout.
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { checkRepository } from "./engine.mjs";

/** A registry entry that is valid until a test breaks it. */
export const validEntry = {
  id: "LX-0001",
  tool: "oxlint",
  rules: ["no-console"],
  file: "web/src/example.ts",
  kind: "inline",
  count: 1,
  reason: "The diagnostic harness prints to the console on purpose.",
  owner: "project maintainers",
  reviewOn: "2999-01-01",
};

/**
 * @param {Record<string, unknown>[]} exceptions
 * @param {Record<string, unknown>[]} [generated]
 */
export function registryText(exceptions, generated = []) {
  return JSON.stringify({ version: 2, generated, exceptions });
}

/**
 * Build a tree from `files`, run `action` with its root, and always remove it.
 * @template T
 * @param {Record<string, string>} files Repository-relative path to content.
 * @param {(root: string) => T} action
 * @returns {T}
 */
export function withTree(files, action) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-lint-tree-"));
  try {
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    return action(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

/**
 * @param {Record<string, string>} files
 * @param {Record<string, unknown>[]} exceptions
 * @returns {string[]}
 */
export function findings(files, exceptions) {
  return withTree(
    { "config/lint-exceptions.json": registryText(exceptions), ...files },
    (root) => checkRepository(root).report.errors,
  );
}
