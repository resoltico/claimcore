import { expect, test } from "./fixtures";
import { configureDimension } from "./design-matrix-checks";
import { geometryReport, isWideTable, tableLayoutState } from "./design-geometry";
import { prepareWorklistDensity } from "./design-matrix-fixtures";

test("ordinary wide worklists meet row and chip density", async ({ browser, page, baseURL }) => {
  test.setTimeout(120_000);
  const { marker, cookies } = await prepareWorklistDensity(browser, baseURL!);
  await page.context().addCookies(cookies);
  const query = `q=${encodeURIComponent(marker)}`;
  for (const width of [1440, 1366]) {
    await configureDimension(page, { width, language: "en", mode: "light", accent: "blue" });
    for (const [path, count] of [
      [`/cases?${query}`, 6],
      [`/cases?view=ALL&${query}`, 6],
      [`/cases?view=MINE&${query}`, 1],
      [`/reminders?${query}`, 2],
      [`/reminders?view=MINE&${query}`, 1],
      [`/people?${query}`, 1],
    ] as const) {
      await page.goto(path);
      const list = page.locator("main .data-table");
      const rows = list.locator("tbody tr:not(.table-group)");
      const seededRows = rows.filter({ hasText: marker });
      await expect(seededRows, path).toHaveCount(count);
      if (path.startsWith("/cases")) {
        await expect(rows.filter({ hasNotText: marker }), path).toHaveCount(0);
      }
      expect(isWideTable(await tableLayoutState(page)), path).toBe(true);
      if (path === `/cases?view=ALL&${query}`) {
        await expect(
          seededRows.filter({
            has: page.locator(".chip-list:has(li:nth-child(3)):not(:has(li:nth-child(4)))"),
          }),
        ).toHaveCount(5);
      }
      const { violations } = await geometryReport(page, { densityMarker: marker });
      expect(
        violations.filter((id) => ["G4", "G5", "G5b", "G5c", "G18"].includes(id)),
        path,
      ).toEqual([]);
    }
  }
});
