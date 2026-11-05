import { expect, type Locator, type Page } from "@playwright/test";
/** Resize completion precedes React's media-query subscription update. */
export const settledWorkspaceHeader = async (page: Page): Promise<void> => {
  await page.locator(".workspace-header").waitFor({ state: "visible" });
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  await page
    .locator(
      compact
        ? ".workspace-mobile-tabs:visible, .case-action-bar:visible"
        : ".workspace-sidebar nav",
    )
    .waitFor({ state: "visible" });
};
/** Compact case tasks retain one bar; use its actual Back action to reach navigation. */
export const compactSettingsNavigation = async (page: Page): Promise<Locator> => {
  const settings = page.locator('.workspace-mobile-tabs a[href="/settings"]');
  if (!(await settings.isVisible())) {
    await page.locator(".case-back button").click();
  }
  await expect(settings).toBeVisible();
  return settings;
};
/** Desktop account controls live in the sidebar footer. Compact sign out lives in Settings. */
export const showAccountActions = async (page: Page): Promise<void> => {
  const drawerSettings = page.locator(
    '[role="dialog"] [data-open-settings], [role="dialog"] button:has(use[href="#claimcore-icon-settings"])',
  );
  if (await drawerSettings.isVisible()) {
    await drawerSettings.click();
    return;
  }
  await settledWorkspaceHeader(page);
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  if (compact) {
    await (await compactSettingsNavigation(page)).click();
    return;
  }
  const account = page.locator(".workspace-sidebar .account-menu");
  if ((await account.getAttribute("open")) === null) {
    await account.locator(":scope > summary").click();
  }
};
export const chooseChoice = (page: Page, name: string, value: string): Promise<void> =>
  page.locator(`input[type="radio"][name="${name}"][value="${value}"]`).check();
export const choiceGroup = (page: Page, name: string): Locator =>
  page.getByRole("radiogroup").filter({ has: page.locator(`input[name="${name}"]`) });
export const showExactLookup = async (page: Page): Promise<Locator> => {
  await page.locator('.worklist[aria-busy="false"]').waitFor({ state: "visible" });
  await page.locator('.summary-band[aria-busy="true"]').waitFor({ state: "detached" });
  const input = page.locator("#case-lookup");
  if (!(await input.isVisible())) {
    await page.locator(".exact-lookup > summary").click();
  }
  return input;
};
export const goToView = async (
  page: Page,
  name: "cases" | "people" | "reminders" | "authority-history",
): Promise<void> => {
  await settledWorkspaceHeader(page);
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  const navigation = page.locator(compact ? ".workspace-mobile-tabs" : ".workspace-sidebar nav");
  await navigation.locator(`a[href="/${name}"]`).click();
};
/** Recovery is intentionally absent from primary navigation; an exact direct link is supported. */
export const openRecovery = async (page: Page): Promise<void> => {
  await page.goto("/recovery", { waitUntil: "commit" });
};

/** General receipt observation is secondary to the actor's dedicated recovery list. */
export const showOperationLookup = async (page: Page): Promise<Locator> => {
  const input = page.locator('section[aria-labelledby="operation-lookup-title"] input');
  if (!(await input.isVisible())) {
    await page
      .locator("details")
      .filter({ has: page.locator("#operation-lookup-title") })
      .locator(":scope > summary")
      .click();
  }
  return input;
};

/** Select a visible closed value through the shared CSP-safe listbox. */
export const chooseOption = async (page: Page, control: Locator, value: string): Promise<void> => {
  await expect(control).toBeEnabled();
  if ((await control.getAttribute("aria-expanded")) !== "true") {
    // Finish the automation's scroll before opening a scroll-dismissed popup.
    await control.scrollIntoViewIfNeeded();
    await control.evaluate(
      () =>
        new Promise<void>((resolve) => {
          requestAnimationFrame(() => {
            requestAnimationFrame(() => {
              resolve();
            });
          });
        }),
    );
    await control.click();
  }
  const listbox = page.getByRole("listbox");
  await expect(listbox).toBeVisible();
  const options = await listbox.getByRole("option").all();
  for (const option of options) {
    if ((await option.getAttribute("data-key")) === value) {
      await expect(option).toBeEnabled();
      await option.click();
      await expect(listbox).toHaveCount(0);
      return;
    }
  }
  throw new Error("E2E_SELECT_VALUE_NOT_OFFERED");
};
