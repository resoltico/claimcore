// @vitest-environment node
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { expect, it } from "vitest";

const runFixture = (
  source: string,
  tool: string,
  argumentsFor: (fixture: string) => string[],
  config?: string,
) => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-test-completion-"));
  try {
    const fixture = join(root, "probe.test.ts");
    writeFileSync(fixture, source);
    if (config !== undefined) {
      symlinkSync(resolve("node_modules"), join(root, "node_modules"), "dir");
      writeFileSync(join(root, "vitest.config.mjs"), config);
    }
    return spawnSync(process.execPath, [resolve(tool), ...argumentsFor(fixture)], {
      cwd: root,
      encoding: "utf8",
      timeout: 10_000,
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
};

it.each(["test.skip", "test.fixme", "test.fail", "it.fails"])(
  "completion lint rejects the authored %s API",
  (method) => {
    const result = runFixture(
      `${method}("probe", () => { expect(true).toBe(true); });`,
      "node_modules/oxlint/bin/oxlint",
      (fixture) => ["--config", resolve("test-completion-lint.config.ts"), fixture],
    );
    expect(result.status, result.stdout + result.stderr).toBe(1);
    expect(result.stdout + result.stderr).toContain("no-restricted-properties");
  },
);

it.each([
  ["test", "true", 0],
  ["test.fails", "false", 1],
] as const)("required reporter accepts only ordinary success from %s", (method, actual, status) => {
  const reporter = pathToFileURL(resolve("scripts/required-test-reporter.mjs")).href;
  const result = runFixture(
    `import { expect, test } from "vitest";
${method}("probe", () => { expect(${actual}).toBe(true); });`,
    "node_modules/vitest/vitest.mjs",
    () => ["run", "--configLoader", "runner"],
    `import Reporter from ${JSON.stringify(reporter)};
export default { test: { allowOnly: false, reporters: ["dot", new Reporter()] } };`,
  );
  const output = result.stdout + result.stderr;
  expect(result.status, output).toBe(status);
  if (status === 1) {
    expect(output).toContain("Required test completion refused");
  }
});
