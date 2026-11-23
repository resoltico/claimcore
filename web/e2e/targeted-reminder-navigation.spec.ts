import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";
import { selectLanguage } from "./localization-support";
const reminderResponse = (page: Page) =>
  page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/reminder.list" &&
      response.request().method() === "POST",
  );
test("discovers a case reminder queue, retains visible context through filters and reload, then clears only that case @engine", async ({
  page,
}) => {
  test.setTimeout(60_000);
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const reference = `TARGET-${randomUUID()}`;
  await openCase(page, reference);
  const entry = page.getByRole("link", { name: "Reminders for this case", exact: true });
  await expect(entry).toBeVisible();
  const first = reminderResponse(page);
  await entry.click();
  expect((await first).request().postDataJSON()).toEqual({
    target: { kind: "REFERENCE", caseReference: reference },
    state: "ANY",
    includeInactive: true,
    limit: 50,
  });
  await expect(page.getByRole("heading", { name: "Reminders", exact: true })).toBeVisible();
  await expect(page.getByText(reference, { exact: true })).toBeVisible();
  await page.locator('.reminder-queue button[aria-haspopup="dialog"]').click();
  await expect(page.getByRole("checkbox", { name: "Include inactive case work" })).toBeChecked();
  await page.keyboard.press("Escape");
  await page.getByRole("combobox", { name: "Reminder due", exact: true }).click();
  const due = reminderResponse(page);
  await page.getByRole("option", { name: "Today", exact: true }).click();
  await due;
  await page.getByRole("combobox", { name: "Responsibility", exact: true }).click();
  const assignment = reminderResponse(page);
  // Firefox's locator click scrolls the document and dismisses this nonmodal listbox.
  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("option", { name: "Mine", exact: true })).toBeFocused();
  await page.keyboard.press("Enter");
  await assignment;
  const search = page.getByRole("searchbox", { name: "Filter this view", exact: true });
  await search.fill("synthetic targeted action");
  const searched = reminderResponse(page);
  await search.press("Enter");
  const selected = {
    target: { kind: "REFERENCE", caseReference: reference },
    due: "TODAY",
    assignment: "MINE",
    text: "synthetic targeted action",
    state: "ANY",
    includeInactive: true,
    limit: 50,
  };
  expect((await searched).request().postDataJSON()).toEqual(selected);
  await expect(page.getByText(reference, { exact: true })).toBeVisible();
  const reloaded = reminderResponse(page);
  await page.reload();
  expect((await reloaded).request().postDataJSON()).toEqual(selected);
  await expect(page.getByText(reference, { exact: true })).toBeVisible();
  const cleared = reminderResponse(page);
  await page.getByRole("button", { name: "Clear case restriction", exact: true }).click();
  expect((await cleared).request().postDataJSON()).toEqual({
    due: "TODAY",
    assignment: "MINE",
    text: "synthetic targeted action",
    state: "ANY",
    includeInactive: true,
    limit: 50,
  });
  await expect(page.getByText(reference, { exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Reminders", exact: true })).toBeFocused();
  expect(new URL(page.url()).searchParams.has("caseReference")).toBe(false);
});

test("focus and visibility revalidate the open reminder queue only once @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.goto("/reminders?view=PENDING");
  await expect(page.locator('.reminder-list[aria-busy="false"]')).toBeVisible();

  const readPaths = ["/api/context", "/api/work.views", "/api/reminder.list"];
  const reads: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (readPaths.includes(path)) {
      reads.push(path);
    }
  });
  const resumed = reminderResponse(page);
  await page.evaluate(() => {
    window.dispatchEvent(new Event("focus"));
  });
  expect(await (await resumed).finished()).toBeNull();
  await expect.poll(() => [...reads].sort()).toEqual([...readPaths].sort());

  const secondPass = page.waitForRequest(
    (request) => readPaths.includes(new URL(request.url()).pathname),
    { timeout: 1_000 },
  );
  await page.evaluate(() => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(secondPass).rejects.toThrow(/Timeout/u);
  expect([...reads].sort()).toEqual([...readPaths].sort());
});
