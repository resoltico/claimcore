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
    const observed = reminders.getByText(/^Work observed for /u).first();
    const empty = reminders.getByText("No reminders match this view.", { exact: true });
    // A successful empty page can show both its message and its observation basis.
    await expect(observed.or(empty).first()).toBeVisible();
  }
  const overview = page.locator(".work-overview");
  await expect(overview).toHaveAttribute("aria-busy", "false");
  await expect(overview.getByText(/^Work observed for /u)).toBeVisible();
};
