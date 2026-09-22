import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { startOpen } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";
import {
  expectKeyboardContained,
  confirmPrepared,
  pauseJsonReply,
  preparedFrom,
  selectFormat,
  selectLanguage,
  trackRequests,
} from "./localization-support";

test("preserves authored Unicode and invalid calendar text while localizing real core validation", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `LOCALE-VALIDATE-${randomUUID()}`);
  const name = page.locator('input[name="claimantName"]');
  const date = page.locator('input[name="incidentDate"]');
  await name.fill("A\u0308 / العربية");
  await date.fill("2026-02-30");
  await name.evaluate((element: HTMLInputElement) => element.setSelectionRange(1, 3));
  const originalNode = await name.elementHandle();
  const requests = trackRequests(page);
  for (const language of ["lv", "ar", "en-XA", "en"] as const) await selectLanguage(page, language);
  expect(await name.evaluate((element, prior) => element === prior, originalNode)).toBe(true);
  expect(
    await name.evaluate((element: HTMLInputElement) => [
      element.selectionStart,
      element.selectionEnd,
    ]),
  ).toEqual([1, 3]);
  await expect(name).toHaveValue("A\u0308 / العربية");
  await expect(date).toHaveValue("2026-02-30");
  expect(requests).toHaveLength(0);
  const pending = await pauseJsonReply(page, "command.prepare");
  try {
    await page.getByRole("button", { name: "Prepare exact request" }).click();
    const captured = await pending.ready;
    expect(captured.reply.outcome.tag).toBe("REJECTED");
    await selectLanguage(page, "lv");
    expect(requests).toHaveLength(1);
    pending.release();
    await expect(date).toBeFocused();
    await expect(date).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByRole("alert")).toBeVisible();
    await selectLanguage(page, "ar");
    await expect(date).toHaveValue("2026-02-30");
    expect(requests).toHaveLength(1);
    await expectAccessible(page);
  } finally {
    pending.release();
  }
});

test("switches language and independent display format through native preparation without changing review consent", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `LOCALE-PREPARE-${randomUUID()}`);
  const requests = trackRequests(page);
  const pending = await pauseJsonReply(page, "command.prepare");
  try {
    await page.getByRole("button", { name: "Prepare exact request" }).click();
    const captured = await pending.ready;
    const identity = preparedFrom(captured.reply);
    await selectLanguage(page, "lv");
    await selectFormat(page, "ar-EG");
    await selectLanguage(page, "ar");
    expect(requests).toHaveLength(1);
    pending.release();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    const originalDialog = await dialog.elementHandle();
    const confirmation = dialog.getByRole("checkbox");
    await confirmPrepared(page);
    await expectKeyboardContained(page, dialog);
    await selectLanguage(page, "en-XA");
    await selectFormat(page, "lv-LV");
    expect(await dialog.evaluate((element, prior) => element === prior, originalDialog)).toBe(true);
    await expect(confirmation).toBeChecked();
    await expect(dialog).toContainText(identity.operationId);
    await expect(dialog).toContainText(identity.requestSha256);
    expect(requests).toHaveLength(1);
    await expectAccessible(page);
    await selectLanguage(page, "en");
    await expect(confirmation).toBeChecked();
    await page.getByRole("button", { name: "Keep for Recovery" }).click();
    await expect(page.getByRole("button", { name: "Prepare exact request" })).toBeFocused();
  } finally {
    pending.release();
  }
});
