import { expect, type Page, type Locator } from "@playwright/test";
import { parsePreferences, preferenceKey } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";

const expectReceiptReadSettled = async (
  page: Page,
  p: ReturnType<typeof createPresenter>,
): Promise<void> => {
  const receipt = page.locator(".success-toast");
  if ((await receipt.count()) > 0) {
    for (let attempt = 0; attempt < 3; attempt += 1) {
      await expect(
        receipt.getByRole("status").filter({ hasText: p.text("ui.loading") }),
      ).toHaveCount(0);
      const pressure = receipt.getByRole("alert").filter({ hasText: p.text("host.RATE_LIMITED") });
      if (!(await pressure.isVisible())) {
        break;
      }
      await receipt.getByRole("button", { name: p.text("ui.tryAgain"), exact: true }).click();
    }
    await expect(receipt.getByRole("alert")).toHaveCount(0);
  }
};

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
  const workspaceBusy = main
    .getByRole("alert")
    .filter({ hasText: p.text("host.RATE_LIMITED") })
    .filter({ has: page.getByRole("button", { name: p.text("ui.tryAgain"), exact: true }) });
  const busy = current
    .locator(":scope > [role=alert]")
    .filter({ hasText: p.text("host.RATE_LIMITED") });
  let workspaceRetries = 0;
  let caseRetries = 0;
  // Scope changes can mask a formerly ready case between observations. Retry only a
  // currently displayed admission refusal, then require the complete read state again.
  await expect(async () => {
    if ((await workspaceBusy.isVisible()) && workspaceRetries < 3) {
      workspaceRetries += 1;
      await workspaceBusy
        .getByRole("button", { name: p.text("ui.tryAgain"), exact: true })
        .click({ timeout: 1000 });
    }
    if ((await busy.isVisible()) && caseRetries < 3) {
      caseRetries += 1;
      await current
        .locator(":scope > button")
        .filter({ hasText: p.text("ui.tryAgain") })
        .click({ timeout: 1000 });
    }
    await expect(current).toBeVisible();
    await expect(header).toBeVisible();
    await expect(
      current
        .locator(":scope > [role=status]")
        .filter({ hasText: p.text("ui.loadingCurrentCase") }),
    ).toHaveCount(0);
    await expect(current.locator(":scope > .error")).toHaveCount(0);
  }).toPass({ timeout: 15000 });
  await expectReceiptReadSettled(page, p);
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
