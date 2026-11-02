import { expect, type Locator, type Page } from "@playwright/test";

const background = (button: Locator) =>
  button.evaluate((element) => getComputedStyle(element).backgroundColor);

const checkCaseActions = async (page: Page, expected?: string | null) => {
  const region = page.locator(".case-heading, .case-actions, .case-action-bar");
  if ((await region.locator("button").count()) === 0 && expected === undefined) {
    return;
  }
  const primary = region.locator("button.primary");
  if (expected === null) {
    await expect(primary).toHaveCount(0);
    return;
  }
  await expect(primary).toHaveCount(1);
  await expect(primary).not.toHaveClass(/case-action-danger/u);
  if (expected !== undefined) {
    await expect(primary).toHaveAttribute("data-act", expected);
  }
  const fill = await background(primary);
  const secondary = region.locator("button:not(.primary):not(.case-action-danger)");
  for (const button of await secondary.all()) {
    expect(
      await background(button),
      "Secondary case acts must differ from the primary act",
    ).not.toBe(fill);
  }
  const danger = region.locator("button.case-action-danger");
  if (
    (await danger.count()) > 0 &&
    !(await primary.evaluate((element) => element.classList.contains("case-action-danger")))
  ) {
    expect(await background(danger), "Erasure must retain its danger treatment").not.toBe(fill);
  }
};

const checkSavedViewSelection = async (page: Page) => {
  const selected = page.locator('.saved-views button[aria-pressed="true"]');
  if ((await selected.count()) === 0) {
    return;
  }
  const fill = await background(selected);
  for (const button of await page.locator('.saved-views button[aria-pressed="false"]').all()) {
    expect(
      await background(button),
      "The selected saved view must remain visually distinct",
    ).not.toBe(fill);
  }
};

export const checkActionHierarchy = async (page: Page, expected?: string | null) => {
  await checkCaseActions(page, expected);
  await checkSavedViewSelection(page);
  const primary = page.locator("main button.primary, main button[type=submit]").first();
  if ((await primary.count()) === 0) {
    return;
  }
  const fill = await background(primary);
  for (const button of await page.locator(".workspace-header button").all()) {
    expect(
      await background(button),
      "Shell controls must not compete with the page action",
    ).not.toBe(fill);
  }
  const form = page.locator(".operation-editor form");
  for (const button of await form.locator("button:not([type=submit])").all()) {
    expect(await background(button), "Back and input controls must remain secondary").not.toBe(
      fill,
    );
  }
};
