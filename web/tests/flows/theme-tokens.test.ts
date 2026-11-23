import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { execFileSync } from "node:child_process";
const css = (directory: string): string[] =>
  readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const filename = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      return css(filename);
    }
    return entry.name.endsWith(".css") ? [filename] : [];
  });
it("owns every CSS hex color in tokens.css and viewport queries in layout.css", () => {
  const stylesheets = [...css(resolve("src")), ...css(resolve("public"))];
  for (const path of stylesheets.filter((filename) => !filename.endsWith("/theme/tokens.css"))) {
    expect(readFileSync(path, "utf8"), path).not.toMatch(/#[0-9a-f]{3,8}\b/iu);
  }
  for (const path of stylesheets.filter((filename) => !filename.endsWith("/ui/layout.css"))) {
    expect(readFileSync(path, "utf8"), path).not.toMatch(/@media[^{}]*\b(?:width|height)\b/u);
  }
});

const missingTokens = (sources: ReadonlyArray<string>): string[] => {
  const text = sources.join("\n");
  const declared = new Set([...text.matchAll(/(--[\w-]+)\s*:/gu)].map((match) => match[1]!));
  const references = new Set(
    [...text.matchAll(/var\(\s*(--[\w-]+)\s*\)/gu)].map((match) => match[1]!),
  );
  return [...references].filter((name) => !declared.has(name));
};
it("declares every referenced CSS token without a fallback", () => {
  const themeCss = execFileSync(
    process.execPath,
    [
      "--input-type=module",
      "-e",
      "import { themeCss } from './scripts/generate-theme.mjs'; process.stdout.write(themeCss);",
    ],
    { encoding: "utf8" },
  );
  const sources = [
    themeCss,
    ...[...css(resolve("src")), ...css(resolve("public"))].map((path) =>
      readFileSync(path, "utf8"),
    ),
  ];
  expect(missingTokens(sources)).toEqual([]);
  expect(
    missingTokens([...sources, ".witness { font-family: var(--undeclared-font-witness); }"]),
  ).toEqual(["--undeclared-font-witness"]);
});
