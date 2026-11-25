// @vitest-environment node
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { expect, it } from "vitest";

const check = (source: string) => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-message-lint-"));
  try {
    const config = join(root, ".oxlintrc.json");
    const fixture = join(root, "fixture.ts");
    writeFileSync(
      config,
      JSON.stringify({
        jsPlugins: [
          {
            name: "presenter-message",
            specifier: resolve("scripts/presenter-message-lint.plugin.mjs"),
          },
        ],
        rules: { "presenter-message/whole-messages": "error" },
      }),
    );
    writeFileSync(fixture, source);
    return spawnSync(
      process.execPath,
      [resolve("node_modules/oxlint/bin/oxlint"), "-c", config, fixture],
      {
        cwd: root,
        encoding: "utf8",
      },
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
};

it.each([
  `\`\${p.text("ui.filters")} (\${p.integer(count)})\``,
  'renderCatalogKey(p, titleId) + " · " + p.integer(count)',
  '"Filters: " + (active ? p.integer(count) : "")',
])("rejects assembly of presenter output: %s", (source) => {
  const result = check(source);
  expect(result.status, result.stderr).toBe(1);
  expect(result.stdout).toContain(
    "Compose presenter output in a catalog message with named arguments.",
  );
});

it("accepts a catalog message with named arguments", () => {
  const result = check('p.text("ui.filtersWithCount", { count: p.integer(count) });');
  expect(result.status, result.stdout + result.stderr).toBe(0);
});
