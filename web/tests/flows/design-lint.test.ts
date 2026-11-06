// @vitest-environment node
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { spawnSync } from "node:child_process";
import { expect, it } from "vitest";
const inspect = (text: string): string => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-theme-lint-"));
  try {
    mkdirSync(join(root, "src"));
    mkdirSync(join(root, "public"));
    writeFileSync(join(root, "index.html"), "<!doctype html>");
    writeFileSync(join(root, "src/control.css"), text);
    const module = pathToFileURL(resolve("scripts/design-lint.mjs")).href;
    const script = `import { designFindings } from ${JSON.stringify(module)}; process.stdout.write(JSON.stringify(designFindings(process.argv[1])));`;
    const result = spawnSync(process.execPath, ["--input-type=module", "-e", script, root], {
      encoding: "utf8",
    });
    expect(result.status, result.stderr).toBe(0);
    return result.stdout;
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
};
it.each([
  ['<select aria-label="Choice"></select>', "native select"],
  [".control {color: rgb(1 2 3)}", "colour literals"],
  [".control {color: rebeccapurple}", "named colour literal"],
  [".control {color: var(--green-700)}", "semantic colours"],
  ["@media (width < 40rem) {.control {display:block}}", "viewport media queries"],
  [".control {font-size: 14px}", "font sizes"],
])("rejects an independent design-policy violation: %s", (input, reason) => {
  expect(inspect(input)).toContain(reason);
});
it("accepts semantic tokens, container queries and system colours", () => {
  expect(
    inspect(
      ".control {color: var(--text-primary); border: 1px solid ButtonText; background: transparent; font-size: 1rem} @container (width < 20rem) {.control {display:block}}",
    ),
  ).toBe("[]");
});
