import type { Locator, Page } from "@playwright/test";
/** Resize completion precedes React's media-query subscription update. */
export const settledWorkspaceHeader = async (page: Page): Promise<void> => {
  await page.locator(".workspace-header").waitFor({ state: "visible" });
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  await page
    .locator(".workspace-header button[aria-expanded]")
    .waitFor({ state: compact ? "visible" : "detached" });
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
  name: "cases" | "operations" | "authority",
): Promise<void> => {
  await settledWorkspaceHeader(page);
  const link = page.locator(`nav a[href="/${name}"]`);
  // The header mounts before the current-context reply supplies authorized navigation.
  await link.waitFor({ state: "attached" });
  if (!(await link.isVisible())) {
    await page.locator(".workspace-header button[aria-expanded]").click();
  }
  await link.click();
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
