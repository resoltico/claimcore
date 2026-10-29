import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
const css = (directory: string): string[] =>
  readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const filename = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      return css(filename);
    }
    return entry.name.endsWith(".css") ? [filename] : [];
  });
it("owns every CSS hex color in tokens.css and keeps media breakpoints aligned with tokens", () => {
  const root = resolve("src");
  for (const path of css(root).filter((filename) => !filename.endsWith("/theme/tokens.css"))) {
    expect(readFileSync(path, "utf8"), path).not.toMatch(/#[0-9a-f]{3,8}\b/iu);
  }
  const tokens = readFileSync(resolve(root, "theme/tokens.css"), "utf8");
  expect(tokens).toContain("--breakpoint-compact: 40rem");
  expect(tokens).toContain("--breakpoint-wide: 64rem");
  for (const path of css(root)) {
    for (const match of readFileSync(path, "utf8").matchAll(
      /@media\s*\(width\s*(?:<|>=)\s*([0-9]+rem)\)/gu,
    )) {
      expect(["40rem", "64rem"]).toContain(match[1]);
    }
  }
});
