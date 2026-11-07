import { expect, type Page, type Locator } from "@playwright/test";
import { languages } from "../src/presentation/preferences";
import { catalogLanguage } from "../src/presentation/languageCatalog";
import { ui } from "./localization-support";

/** Exercise the user's explicit retry when a read reports server admission pressure. */
export const expectCaseReadsSettled = async (page: Page): Promise<void> => {
  const tag = await page.locator("html").getAttribute("lang");
  const language = catalogLanguage(languages.find((candidate) => candidate === tag) ?? "en");
  const retryBusy = async (scope: Locator) => {
    const busy = scope.getByRole("alert").filter({ hasText: ui(language, "host.RATE_LIMITED") });
    if (await busy.isVisible()) {
      await scope.getByRole("button", { name: ui(language, "ui.tryAgain"), exact: true }).click();
    }
  };
  const current = page.locator(".case-detail");
  const header = current.locator(".case-header-state");
  const workspaceBusy = page
    .locator("main > [role=alert]")
    .filter({ hasText: ui(language, "host.RATE_LIMITED") });
  const busy = current.getByRole("alert").filter({ hasText: ui(language, "host.RATE_LIMITED") });
  await expect
    .poll(
      async () =>
        (await header.isVisible()) || (await workspaceBusy.isVisible()) || (await busy.isVisible()),
    )
    .toBe(true);
  if (await workspaceBusy.isVisible()) {
    await workspaceBusy
      .getByRole("button", { name: ui(language, "ui.tryAgain"), exact: true })
      .click();
    await expect(workspaceBusy).toHaveCount(0);
  }
  if (!(await header.isVisible())) {
    await retryBusy(current);
  }
  await expect(header).toBeVisible();
  await expect(current.locator(":scope > .error")).toHaveCount(0);
  for (const panel of [
    current.locator(".reminder-list"),
    current.locator(".case-timeline-panel"),
  ]) {
    if ((await panel.count()) > 0) {
      await expect(panel).toHaveAttribute("aria-busy", "false");
      await retryBusy(panel);
      await expect(panel).toHaveAttribute("aria-busy", "false");
      await expect(panel.getByRole("alert")).toHaveCount(0);
    }
  }
};
