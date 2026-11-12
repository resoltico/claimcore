import { expect, type Page, type Locator } from "@playwright/test";
import { parsePreferences, preferenceKey } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";

/** Exercise the user's explicit retry when a read reports server admission pressure. */
export const expectCaseReadsSettled = async (page: Page): Promise<void> => {
  const preferences = parsePreferences(
    await page.evaluate((key) => localStorage.getItem(key), preferenceKey),
  );
  const p = createPresenter(preferences);
  const retryBusy = async (scope: Locator) => {
    const busy = scope.getByRole("alert").filter({ hasText: p.text("host.RATE_LIMITED") });
    if (await busy.isVisible()) {
      await scope.getByRole("button", { name: p.text("ui.tryAgain"), exact: true }).click();
    }
  };
  const current = page.locator(".case-detail");
  const header = current
    .locator('.case-header-state, .region-notice[data-tone="FINAL"]')
    .filter({ visible: true })
    .first();
  const main = page.locator("main");
  const workspaceBusy = main.getByRole("alert").filter({ hasText: p.text("host.RATE_LIMITED") });
  for (let attempt = 0; attempt < 3; attempt += 1) {
    await expect
      .poll(async () => (await current.count()) > 0 || (await workspaceBusy.isVisible()))
      .toBe(true);
    if ((await current.count()) > 0) {
      break;
    }
    await main.getByRole("button", { name: p.text("ui.tryAgain"), exact: true }).click();
  }
  await expect(current).toBeVisible();
  const busy = current
    .locator(":scope > [role=alert]")
    .filter({ hasText: p.text("host.RATE_LIMITED") });
  for (let attempt = 0; attempt < 3; attempt += 1) {
    await expect
      .poll(async () => (await header.isVisible()) || (await busy.isVisible()))
      .toBe(true);
    if (await header.isVisible()) {
      break;
    }
    await current
      .locator(":scope > button")
      .filter({ hasText: p.text("ui.tryAgain") })
      .click();
  }
  await expect(header).toBeVisible();
  await expect(
    current.locator(":scope > [role=status]").filter({ hasText: p.text("ui.loadingCurrentCase") }),
  ).toHaveCount(0);
  await expect(current.locator(":scope > .error")).toHaveCount(0);
  for (const panel of [
    current.locator(".reminder-list"),
    current.locator(".case-timeline-panel"),
  ]) {
    if ((await panel.count()) > 0) {
      for (let attempt = 0; attempt < 3; attempt += 1) {
        await expect(panel).toHaveAttribute("aria-busy", "false");
        const pressure = panel.getByRole("alert").filter({ hasText: p.text("host.RATE_LIMITED") });
        if (!(await pressure.isVisible())) {
          break;
        }
        await retryBusy(panel);
      }
      await expect(panel).toHaveAttribute("aria-busy", "false");
      await expect(panel.getByRole("alert")).toHaveCount(0);
    }
  }
};
