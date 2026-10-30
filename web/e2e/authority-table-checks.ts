import { expect, type Page } from "@playwright/test";
export const checkAuthorityTable = async (page: Page, desktop: boolean) => {
  const table = page.locator(".authority-table");
  if ((await table.count()) === 0) {
    return;
  }
  await expect(table).toHaveRole("table");
  await expect(table.getByRole("columnheader")).toHaveCount(4);
  await expect(table.locator("tbody tr").first()).toBeVisible();
  const layout = await table.evaluate((element) => ({
    table: getComputedStyle(element).display,
    rows: Array.from(element.querySelectorAll("tbody tr"), (row) => ({
      display: getComputedStyle(row).display,
      height: row.getBoundingClientRect().height,
    })),
  }));
  expect(layout.rows.length).toBeGreaterThan(0);
  if (desktop) {
    expect(layout.table).toBe("table");
    await expect(table.locator("thead")).toBeVisible();
    for (const row of layout.rows) {
      expect(row.display).toBe("table-row");
    }
  }
};
