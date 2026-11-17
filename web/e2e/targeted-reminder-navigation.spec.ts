import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
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
  await expect(page.getByRole("checkbox", { name: "Include inactive case work" })).toBeChecked();
  await page.getByRole("combobox", { name: "Reminder due", exact: true }).click();
  const due = reminderResponse(page);
  await page.getByRole("option", { name: "Today", exact: true }).click();
  await due;
  await page.getByRole("combobox", { name: "Responsibility", exact: true }).click();
  const assignment = reminderResponse(page);
  await page.getByRole("option", { name: "Mine", exact: true }).click();
  await assignment;
  const search = page.getByRole("search", { name: "Reminders", exact: true });
  await search
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("synthetic targeted action");
  const searched = reminderResponse(page);
  await search.getByRole("button", { name: "Search", exact: true }).click();
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
