import { expect, type Locator, type Page } from "@playwright/test";

const background = (button: Locator) =>
  button.evaluate((element) => getComputedStyle(element).backgroundColor);

const checkCaseActions = async (page: Page, expected?: string | null) => {
  const region = page.locator(".subject-header-actions");
  if ((await region.locator("button").count()) === 0 && expected === undefined) {
    return;
  }
  const primary = region
    .locator("button.ui-button--primary")
    .or(page.locator(".phone-primary-action button[data-act]"))
    .filter({ visible: true });
  if (expected === undefined && (await primary.count()) === 0) {
    return;
  }
  if (expected === null) {
    await expect(primary).toHaveCount(0);
    return;
  }
  await expect(primary).toHaveCount(1);
  await expect(primary).not.toHaveClass(/ui-button--danger/u);
  if (expected !== undefined) {
    await expect(primary).toHaveAttribute("data-act", expected);
  }
  const fill = await background(primary);
  const secondary = region.locator("button:not(.ui-button--primary):not(.ui-button--danger)");
  for (const button of await secondary.all()) {
    expect(
      await background(button),
      "Secondary case acts must differ from the primary act",
    ).not.toBe(fill);
  }
  const danger = region.locator("button.ui-button--danger");
  if (
    (await danger.count()) > 0 &&
    !(await primary.evaluate((element) => element.classList.contains("ui-button--danger")))
  ) {
    expect(await background(danger), "Erasure must retain its danger treatment").not.toBe(fill);
  }
};

const checkViewSelection = async (page: Page) => {
  const tabs = page.locator('.view-tabs [role="tab"]');
  if ((await tabs.count()) === 0) {
    return;
  }
  const current = page.locator('.view-tabs [role="tab"][aria-selected="true"]');
  await expect(current).toHaveCount(1);
  const style = await current.evaluate((element) => ({
    color: getComputedStyle(element).color,
    border: getComputedStyle(element).borderBottomColor,
  }));
  for (const tab of await page.locator('.view-tabs [role="tab"][aria-selected="false"]').all()) {
    const other = await tab.evaluate((element) => ({
      color: getComputedStyle(element).color,
      border: getComputedStyle(element).borderBottomColor,
    }));
    expect(other.border, "The selected core view keeps its distinct underline").not.toBe(
      style.border,
    );
    expect(other.color, "The selected core view keeps its distinct label").not.toBe(style.color);
  }
};

export const checkActionHierarchy = async (page: Page, expected?: string | null) => {
  await checkCaseActions(page, expected);
  await checkViewSelection(page);
  const dialog = page.getByRole("dialog");
  const task = (await dialog.count()) === 0 ? page.locator("main") : dialog;
  const primary = task
    .locator("button.primary, button.ui-button--primary, button[data-confirm-operation]")
    .filter({ visible: true })
    .first();
  if ((await primary.count()) === 0) {
    return;
  }
  const fill = await background(primary);
  for (const button of await page
    .locator(".workspace-sidebar button, .phone-app-bar button:not(.phone-primary-action button)")
    .filter({ visible: true })
    .all()) {
    expect(
      await background(button),
      "Shell controls must not compete with the page action",
    ).not.toBe(fill);
  }
  const form = task.locator(".operation-form");
  for (const button of await form.locator("button:not([type=submit])").all()) {
    expect(await background(button), "Back and input controls must remain secondary").not.toBe(
      fill,
    );
  }
};
