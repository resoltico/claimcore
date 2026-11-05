import { expect, type Page } from "@playwright/test";

/** Raw fixture requests are a separate client from the app's bounded read scheduler. */
export const expectCaseReadsSettled = async (page: Page): Promise<void> => {
  const current = page.locator(".case-detail");
  // A successful current read renders this header, including for an erased case.
  await expect(current.locator(".case-header-state")).toBeVisible();
  await expect(current.getByText("Loading current case…", { exact: true })).toHaveCount(0);
  await expect(current.locator(":scope > .error")).toHaveCount(0);
  const reminders = current.locator(".reminder-list");
  if ((await reminders.count()) > 0) {
    await expect(reminders).toHaveAttribute("aria-busy", "false");
    await expect(reminders.getByRole("alert")).toHaveCount(0);
  }
  const timeline = current.locator(".case-timeline-panel");
  if ((await timeline.count()) > 0) {
    await expect(timeline).toHaveAttribute("aria-busy", "false");
    await expect(timeline.getByRole("alert")).toHaveCount(0);
  }
};
