// @vitest-environment node
import { it, expect } from "vitest";
import { mkdtemp, mkdir, chmod, writeFile, readFile, readdir, stat, rm } from "node:fs/promises";
import { resolve, join, relative } from "node:path";
import { spawnSync } from "node:child_process";

const boundarySpec = (helper: string) => `
import { test, expect } from "@playwright/test";
import { noHorizontalOverflow } from ${JSON.stringify(helper)};
test("private layout output", async () => {
  const page = { evaluate: async () => ({
    clientWidth:320, scrollWidth:400, bodyWidth:320,
    offenders:[{tag:"div", className:"safe !<>name", left:0, right:400, width:400}],
    scrollable:[{tag:"section", className:"internal <>scroll", clientWidth:100, scrollWidth:200}],
    privateValue:"PRIVATE_SENTINEL"
  }) };
  await expect(noHorizontalOverflow(page,"narrow")).rejects.toEqual(new Error("E2E_LAYOUT_NARROW_OVERFLOW"));
});`;
const writeBoundary = async (root: string, output: string) => {
  await writeFile(join(root, "boundary.spec.mjs"), boundarySpec(resolve("e2e/layout-support.ts")), {
    mode: 0o600,
  });
  await writeFile(
    join(root, "playwright.config.mjs"),
    `export default { testDir:${JSON.stringify(root)}, outputDir:${JSON.stringify(output)}, reporter:"list", retries:0, workers:1 };`,
    { mode: 0o600 },
  );
};
const assertPrivateReport = async (root: string, output: string) => {
  const files = (await readdir(output, { recursive: true })).filter((file) =>
    file.endsWith("layout.json"),
  );
  expect(files).toHaveLength(1);
  const report = join(output, files[0]!);
  expect(relative(output, report)).toMatch(/^[^/]+\/layout\.json$/u);
  expect((await stat(root)).mode & 0o777).toBe(0o700);
  expect((await stat(report)).mode & 0o777).toBe(0o600);
  const text = await readFile(report, "utf8");
  expect(text).not.toContain("PRIVATE_SENTINEL");
  expect(JSON.parse(text)).toEqual({
    stage: "narrow",
    clientWidth: 320,
    scrollWidth: 400,
    bodyWidth: 320,
    offenders: [{ tag: "div", className: "safename", left: 0, right: 400, width: 400 }],
    scrollable: [
      { tag: "section", className: "internalscroll", clientWidth: 100, scrollWidth: 200 },
    ],
  });
};
it("uses pinned Playwright's real per-test output directory under a private ancestor and mode0600", async () => {
  const cache = resolve("node_modules/.cache");
  await mkdir(cache, { recursive: true });
  const root = await mkdtemp(join(cache, "layout-boundary-"));
  await chmod(root, 0o700);
  try {
    const output = join(root, "output");
    await writeBoundary(root, output);
    const result = spawnSync(
      process.execPath,
      [
        resolve("node_modules/@playwright/test/cli.js"),
        "test",
        "--config",
        join(root, "playwright.config.mjs"),
      ],
      { encoding: "utf8", timeout: 20000 },
    );
    expect(result.error).toBeUndefined();
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("1 passed");
    await assertPrivateReport(root, output);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
