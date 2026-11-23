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
    writeFileSync(
      join(root, "src/styles.css"),
      "@layer reset, tokens, base, layout, components, views;",
    );
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
  ['<input type="text" />', "form controls and small elements belong in src/ui"],
  ["<textarea />", "form controls and small elements belong in src/ui"],
  ["<small>Caption</small>", "form controls and small elements belong in src/ui"],
  ["@layer components {.control {overflow-wrap:anywhere}}", "word wrapping anywhere is restricted"],
  [
    "@layer components {.identifier-value {overflow-wrap:anywhere}}",
    "word wrapping anywhere is restricted",
  ],
  [".control {color: rgb(1 2 3)}", "colour literals"],
  [".control {color: rebeccapurple}", "named colour literal"],
  [".control {color: var(--green-700)}", "semantic colours"],
  ["@media (width < 40rem) {.control {display:block}}", "viewport media queries"],
  [".control {font-size: 14px}", "font sizes"],
  ['<div style="display: grid">Content</div>', "inline styles"],
  ['<div style={{ display: "grid" }}>Content</div>', "inline styles"],
  ['element.style.setProperty("--size", "1rem")', "inline styles"],
  [".control { display: grid }", "outside a declared cascade layer"],
  ["@layer custom { .control { display: grid } }", "outside a declared cascade layer"],
  [
    "@layer components { .control { display: grid } .control { display: flex } }",
    "repeated top-level selector",
  ],
  ["@layer components { .control { padding: 1rem } }", "spacing or sizing must use tokens"],
  [
    "@layer components { .control { max-inline-size: calc(100% - 1px) } }",
    "spacing or sizing must use tokens",
  ],
  ['@layer components { [data-code="CLOSED"] { display: grid } }', "domain identity selector"],
  ['@layer components { [data-headline="CLOSED"] { display: grid } }', "domain identity selector"],
  ['@layer components { [data-act="case.open"] { display: grid } }', "domain identity selector"],
])("rejects an independent design-policy violation: %s", (input, reason) => {
  expect(inspect(input)).toContain(reason);
});
it("accepts semantic tokens, container queries and system colours", () => {
  expect(
    inspect(
      "@layer components {.control {color: var(--text-primary); border: 1px solid ButtonText; background: transparent; font-size: var(--text-md)} @container (width < 20rem) {.control {display:block}}}",
    ),
  ).toBe("[]");
});
