import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { keepForRecovery, openCase, prepare, startCommand } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";
import {
  inspectPending,
  confirmPrepared,
  pauseJsonReply,
  selectFormat,
  selectLanguage,
  trackRequests,
  ui,
} from "./localization-support";

test("preserves a committed operation and exact recovery identity when its localized submit response is lost", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `LOCALE-LOSS-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  await confirmPrepared(page);
  const requests = trackRequests(page);
  const pending = await pauseJsonReply(page, "command.execute", true);
  try {
    await page.getByRole("button", { name: "Submit exact request" }).click();
    const captured = await pending.ready;
    const outcome = captured.reply.outcome;
    expect(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED").toBe(true);
    expect(captured.bytes.equals(Buffer.from(JSON.stringify(identity)))).toBe(true);
    await selectLanguage(page, "ar");
    await selectFormat(page, "lv-LV");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toBeVisible();
    await expect(page.getByRole("checkbox")).toBeChecked();
    expect(requests).toHaveLength(1);
    pending.release();
    await expect(page.getByRole("alert")).toBeVisible();
    await selectLanguage(page, "en");
    await expect(page.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
    expect(requests).toHaveLength(1);
  } finally {
    pending.release();
  }
  // This explicit reload is recovery action by the operator, never a language-change side effect.
  await page.reload();
  await page.getByRole("button", { name: "Recovery", exact: true }).click();
  await page.getByLabel("Recovery view").selectOption("TERMINAL");
  const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Inspect", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
  await expect(page.getByRole("button", { name: "Resolve exact preparation" })).toHaveCount(0);
  await expectAccessible(page);
});

test("keeps inspected recovery authority and confirmation stable while language changes during native resolution", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `LOCALE-RESOLVE-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  await keepForRecovery(page);
  await inspectPending(page, identity);
  const selected = await page.getByRole("dialog").elementHandle();
  const requests = trackRequests(page);
  await selectLanguage(page, "ar");
  await selectLanguage(page, "lv");
  expect(
    await page.getByRole("dialog").evaluate((element, prior) => element === prior, selected),
  ).toBe(true);
  expect(requests).toHaveLength(0);
  await page.getByRole("button", { name: ui("lv", "ui.resolveExact") }).click();
  const pending = await pauseJsonReply(page, "recovery.resolve");
  try {
    await page.getByRole("button", { name: ui("lv", "ui.confirmResolve") }).click();
    const captured = await pending.ready;
    const outcome = captured.reply.outcome;
    expect(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED").toBe(true);
    expect(captured.bytes.equals(Buffer.from(JSON.stringify(identity)))).toBe(true);
    await selectLanguage(page, "ar");
    await selectFormat(page, "ar-EG");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: ui("ar", "ui.working") })).toBeDisabled();
    await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
    expect(requests).toHaveLength(1);
    await expectAccessible(page);
    pending.release();
    await expect(page.getByRole("status")).toContainText(identity.operationId);
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  } finally {
    pending.release();
  }
});
