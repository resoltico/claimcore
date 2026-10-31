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
  if (!(await link.isVisible())) {
    await page.locator(".workspace-header button[aria-expanded]").click();
  }
  await link.click();
};
