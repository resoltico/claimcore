import { expect, test } from "@playwright/test";
import { isWideTable, stableTableLayout, tableLayoutState } from "./design-geometry";

test("G29 detects a missing gutter and a late wide threshold", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.setContent(`<!doctype html><style>
  html { scrollbar-gutter: stable }
  body { margin: 0; padding-inline-start: 320px }
  main { container: test / inline-size }
  .data-table { width: 100% }
  table { width: 100%; --table-layout: medium }
  @container test (width >= 64rem) { table { --table-layout: wide } }
  html.open { overflow: hidden; scrollbar-gutter: stable }
</style><main><div class="data-table"><table><tbody><tr><td>Cases</td></tr></tbody></table></div></main>`);
  const before = await tableLayoutState(page);
  expect(isWideTable(before)).toBe(true);
  await page.locator("html").evaluate((root) => {
    root.classList.add("open");
  });
  expect(stableTableLayout(before, await tableLayoutState(page))).toBe(true);
  await page.addStyleTag({ content: "html:not(.open) { scrollbar-gutter: auto }" });
  await page.locator("html").evaluate((root) => {
    root.classList.remove("open");
  });
  expect(stableTableLayout(before, await tableLayoutState(page))).toBe(false);
  await page.locator("html").evaluate((root) => {
    root.classList.add("open");
  });
  await page.addStyleTag({
    content: "@container test (width < 70rem) { table { --table-layout: medium } }",
  });
  expect(isWideTable(await tableLayoutState(page))).toBe(false);
});
