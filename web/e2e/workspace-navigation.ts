import { expect, type Locator, type Page } from "@playwright/test";
/** Resize completion precedes React's media-query subscription update. */
export const settledWorkspaceHeader = async (page: Page): Promise<void> => {
  await page.locator(".workspace-shell").waitFor({ state: "visible" });
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  await page
    .locator(compact ? ".workspace-mobile-tabs" : ".workspace-sidebar nav")
    .waitFor({ state: "visible" });
};

export const showAccountActions = async (page: Page): Promise<void> => {
  await settledWorkspaceHeader(page);
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  if (compact) {
    if (!(await page.locator(".shell-more").isVisible())) {
      await page.locator("#workspace-more").click();
    }
    return;
  }
  const trigger = page.locator("#workspace-account");
  if ((await trigger.getAttribute("aria-expanded")) !== "true") {
    await trigger.click();
  }
};
export const workspaceSettingsNavigation = async (page: Page): Promise<Locator> => {
  const dialog = page.locator(
    '[role="dialog"] [data-open-settings], [role="dialog"] button:has(use[href="#claimcore-icon-settings"])',
  );
  if (await dialog.isVisible()) {
    return dialog;
  }
  await showAccountActions(page);
  return page.locator('[data-menu-item="settings"], .shell-more a[href="/settings"]');
};
export const startPeopleCreation = async (page: Page, label = "Add person") => {
  await page.getByRole("button", { name: label, exact: true }).click();
  await page.locator('[data-menu-item="people.admit"]').click();
};
export const chooseChoice = (page: Page, name: string, value: string): Promise<void> =>
  page.locator(`input[type="radio"][name="${name}"][value="${value}"]`).check();
export const choiceGroup = (page: Page, name: string): Locator =>
  page.locator(`[role="radiogroup"][id="field-${name}"]`);
export const showExactLookup = async (page: Page): Promise<Locator> => {
  await page.locator('.worklist[aria-busy="false"]').waitFor({ state: "visible" });
  const input = page.getByRole("dialog").getByRole("combobox");
  if (!(await input.isVisible())) {
    await page.locator(".shell-find:visible").click();
  }
  return input;
};
export const goToView = async (
  page: Page,
  name: "cases" | "people" | "reminders" | "people-history" | "recovery",
): Promise<void> => {
  await settledWorkspaceHeader(page);
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  const navigation = page.locator(compact ? ".workspace-mobile-tabs" : ".workspace-sidebar nav");
  const destination = navigation.locator(`a[href="/${name}"]`);
  if (await destination.isVisible()) {
    await destination.click();
  } else if (name === "recovery") {
    await showAccountActions(page);
    await page
      .getByRole("menuitem", { name: /^Unconfirmed changes/u })
      .or(page.locator('.shell-more a[href="/recovery"]'))
      .click();
  } else if (compact) {
    await page.locator("#workspace-more").click();
    await page.getByRole("dialog").locator(`a[href="/${name}"]`).click();
  } else {
    await destination.click();
  }
};
/** The recovery route also supports an exact direct link. */
export const openRecovery = async (page: Page): Promise<void> => {
  await page.goto("/recovery", { waitUntil: "commit" });
};

/** General receipt observation is secondary to the actor's dedicated recovery list. */
export const showOperationLookup = async (page: Page): Promise<Locator> => {
  const input = page.locator('section[aria-labelledby="operation-lookup-title"] input');
  await input.waitFor({ state: "visible" });
  return input;
};

/** Deliver the automation's scroll before opening a popup that dismisses on scroll. */
export const settlePopupTriggerScroll = async (control: Locator): Promise<void> => {
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
};

/** Select a visible closed value through the shared CSP-safe listbox. */
export const chooseOption = async (page: Page, control: Locator, value: string): Promise<void> => {
  await expect(control).toBeEnabled();
  if ((await control.getAttribute("aria-expanded")) !== "true") {
    await settlePopupTriggerScroll(control);
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
