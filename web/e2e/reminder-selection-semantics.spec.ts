import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";
import { selectLanguage } from "./localization-support";
import { applyPeople } from "./people-setup";
import { operationId } from "../src/operation/operationId";
const nextRead = (page: Page) =>
  page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/reminder.list" &&
      response.request().method() === "POST",
  );
const createReminder = async (page: Page, reference: string, action: string) => {
  const id = operationId();
  await applyPeople(page, {
    operationId: id,
    task: "reminder.create",
    binding: { kind: "REMINDER", target: { kind: "REFERENCE", caseReference: reference } },
    inputs: { "input:reminderAction": action, "input:reminderDueDate": "2027-01-01" },
  });
  return id;
};
const finish = (
  page: Page,
  reference: string,
  id: string,
  task: "reminder.complete" | "reminder.cancel",
) =>
  applyPeople(page, {
    operationId: operationId(),
    task,
    binding: {
      kind: "REMINDER",
      target: { kind: "REFERENCE", caseReference: reference },
      reminderId: id,
      expectedRevision: 1,
    },
    inputs: {},
  });

test("the supplied case link and ordinary Any control expose pending completed and cancelled records @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const reference = `MIXED-${randomUUID()}`;
  await openCase(page, reference);
  const pending = `${reference} pending`;
  const completed = `${reference} completed`;
  const cancelled = `${reference} cancelled`;
  await createReminder(page, reference, pending);
  const completedId = await createReminder(page, reference, completed);
  const cancelledId = await createReminder(page, reference, cancelled);
  await finish(page, reference, completedId, "reminder.complete");
  await finish(page, reference, cancelledId, "reminder.cancel");
  await page.reload();
  const first = nextRead(page);
  await page.getByRole("link", { name: "Reminders for this case", exact: true }).click();
  expect((await first).request().postDataJSON()).toEqual({
    target: { kind: "REFERENCE", caseReference: reference },
    state: "ANY",
    includeInactive: true,
    limit: 50,
  });
  const queue = page.locator(".reminder-queue");
  for (const caption of [pending, completed, cancelled]) {
    await expect(queue.getByText(caption, { exact: true })).toBeVisible();
  }
  await queue.getByRole("combobox", { name: "Reminder state", exact: true }).click();
  const restricted = nextRead(page);
  await page.getByRole("option", { name: "Completed", exact: true }).click();
  expect((await restricted).request().postDataJSON()).toMatchObject({ state: "COMPLETED" });
  await expect(queue.getByText(completed, { exact: true })).toBeVisible();
  await expect(queue.getByText(pending, { exact: true })).toHaveCount(0);
  await expect(queue.getByText(cancelled, { exact: true })).toHaveCount(0);
  await queue.getByRole("combobox", { name: "Reminder state", exact: true }).click();
  const unrestricted = nextRead(page);
  await page.getByRole("option", { name: "Any", exact: true }).click();
  expect((await unrestricted).request().postDataJSON()).toMatchObject({ state: "ANY" });
  for (const caption of [pending, completed, cancelled]) {
    await expect(queue.getByText(caption, { exact: true })).toBeVisible();
  }
});

test("a Completed view shows its owner inactive default and explicit false removes only the closed-case result @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const marker = `INACTIVE-${randomUUID()}`;
  const opened = `${marker}-open`;
  const closed = `${marker}-closed`;
  for (const [reference, caption, close] of [
    [`${marker}-A`, opened, false],
    [`${marker}-B`, closed, true],
  ] as const) {
    await page.goto("/cases");
    await openCase(page, reference);
    const id = await createReminder(page, reference, caption);
    await finish(page, reference, id, "reminder.complete");
    if (close) {
      await applyPeople(page, {
        operationId: operationId(),
        task: "case.close",
        binding: {
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: reference },
          expectedRevision: 1,
        },
        inputs: { "field:outcome": "OTHER" },
      });
    }
  }
  const first = nextRead(page);
  await page.goto(`/reminders?view=COMPLETED&q=${encodeURIComponent(marker)}`);
  expect((await first).request().postDataJSON()).toEqual({
    view: "COMPLETED",
    text: marker,
    limit: 50,
  });
  const queue = page.locator(".reminder-queue");
  const filters = queue.locator('button[aria-haspopup="dialog"]');
  const sheet = page.locator(".filter-sheet").getByRole("dialog");
  await filters.click();
  const inactive = sheet.getByRole("checkbox", { name: "Include inactive case work" });
  await expect(inactive).toBeChecked();
  await expect(inactive).toBeEnabled();
  await expect(queue.getByText(opened, { exact: true })).toBeVisible();
  await expect(queue.getByText(closed, { exact: true })).toBeVisible();
  const restricted = nextRead(page);
  await inactive.uncheck();
  await sheet.locator('button[type="submit"]').click();
  await expect(queue.getByRole("combobox", { name: "Reminder state" })).toContainText("Any");
  expect((await restricted).request().postDataJSON()).toEqual({
    view: "COMPLETED",
    text: marker,
    includeInactive: false,
    limit: 50,
  });
  await expect(queue.getByText(opened, { exact: true })).toBeVisible();
  await expect(queue.getByText(closed, { exact: true })).toHaveCount(0);
  const reloaded = nextRead(page);
  await page.reload();
  expect((await reloaded).request().postDataJSON()).toEqual({
    view: "COMPLETED",
    text: marker,
    includeInactive: false,
    limit: 50,
  });
  await filters.click();
  await expect(inactive).not.toBeChecked();
  await page.keyboard.press("Escape");
  await expect(queue.getByText(closed, { exact: true })).toHaveCount(0);
});
