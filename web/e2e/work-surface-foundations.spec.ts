import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { noHorizontalOverflow } from "./layout-support";
import { casePath } from "../src/routing/routes";

test("case deep links reload and browser back and forward preserve the reference", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `ROUTE.${randomUUID()}/part?#`;
  await openCase(page, reference);
  await expect(page).toHaveURL((url) => url.pathname === casePath(reference));
  await page.reload();
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Back to cases", exact: true }).click();
  await expect(page).toHaveURL(/\/cases$/u);
  await page.goBack();
  await expect(page).toHaveURL((url) => url.pathname === casePath(reference));
  await page.goForward();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
});
for (const theme of ["LIGHT", "DARK"] as const) {
  test(`theme ${theme} passes axe and reflows at 320px`, async ({ page }) => {
    await openAuthenticated(page);
    await page.locator("[data-settings-button]").click();
    await page.getByRole("combobox", { name: "Theme", exact: true }).selectOption(theme);
    await page.keyboard.press("Escape");
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme.toLowerCase());
    await expectAccessible(page);
    await page.setViewportSize({ width: 320, height: 900 });
    await noHorizontalOverflow(page, "narrow");
    await expectAccessible(page);
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme.toLowerCase());
  });
}
