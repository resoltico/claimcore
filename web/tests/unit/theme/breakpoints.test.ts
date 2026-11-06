import { expect, it } from "vitest";
import { breakpoints, compactQuery } from "../../../src/theme/breakpoints";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
const layout = readFileSync(resolve(import.meta.dirname, "../../../src/ui/layout.css"), "utf8");
it("keeps shell CSS and matchMedia thresholds aligned", () => {
  const widths = [...layout.matchAll(/@media\s*\(width\s*[<>=]+\s*(\d+)rem\)/gu)].map((match) =>
    Number(match[1]),
  );
  expect([...new Set(widths)].sort((first, second) => first - second)).toEqual([
    breakpoints.compact,
    breakpoints.wide,
  ]);
  expect(layout).toContain(`@media ${compactQuery}`);
});
