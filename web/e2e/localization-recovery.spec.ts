import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { keepForRecovery, openCase, prepare, startCommand } from "./case-workflow";
import { expectAccessible, openAuthenticated, progress } from "./session-helpers";
import {
  inspectPending,
  confirmPrepared,
  pauseJsonReply,
  selectFormat,
  selectLanguage,
  trackRequests,
  ui,
} from "./localization-support";

const observePreparedBytes = (page: Page): (() => Buffer | null) => {
  let bytes: Buffer | null = null;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/v3/operations/prepare") {
      bytes = Buffer.from(request.postData() ?? "");
    }
  });
  return () => bytes;
};

const returnToRecovery = async (page: Page): Promise<void> => {
  await progress("localized-recovery-reload");
  await page.reload({ waitUntil: "commit" });
  await expect(page.locator("main.app-shell header small")).toBeVisible({ timeout: 10_000 });
  await progress("localized-recovery-definition-ready");
  const navigation = page.getByRole("button", { name: "Recovery", exact: true });
  await expect(navigation).toBeEnabled();
  await navigation.click();
  await expect(navigation).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("heading", { name: "Recovery", exact: true })).toBeVisible();
  await progress("localized-recovery-navigation-ready");
};

const inspectionLanguage = async (page: Page) => {
  for (const language of ["ar", "en", "lv"] as const) {
    await selectLanguage(page, language);
    const dialog = page.getByRole("dialog");
    await expect(dialog).toHaveAccessibleName(ui(language, "ui.recoveryDetails"));
    await expect(
      dialog.getByText(ui(language, "ui.recoveryDetailsHint"), { exact: true }),
    ).toBeVisible();
    await expect(
      dialog.getByRole("button", { name: ui(language, "ui.resolveExact"), exact: true }),
    ).toBeVisible();
    await expect(
      dialog.getByRole("button", { name: ui(language, "ui.close"), exact: true }),
    ).toBeVisible();
  }
};

test("preserves a committed operation and exact recovery identity when its localized submit response is lost", async ({
  page,
}) => {
  test.setTimeout(45_000);
  await openAuthenticated(page);
  await openCase(page, `LOCALE-LOSS-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const preparedBytes = observePreparedBytes(page);
  const identity = await prepare(page);
  await confirmPrepared(page);
  const requests = trackRequests(page);
  const pending = await pauseJsonReply(page, "command.execute", true);
  try {
    await page.getByRole("button", { name: "Record changes" }).click();
    const captured = await pending.ready;
    const { outcome } = captured.reply;
    expect(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED").toBe(true);
    const original = preparedBytes();
    if (original === null) {
      throw new Error("E2E_PREPARE_REQUEST_MISSING");
    }
    expect(captured.bytes.equals(original)).toBe(true);
    await selectLanguage(page, "ar");
    await selectFormat(page, "lv-LV");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("checkbox")).toBeChecked();
    expect(requests).toHaveLength(1);
    pending.release();
    await expect(page.getByRole("alert")).toBeVisible();
    await selectLanguage(page, "en");
    await expect(page.getByRole("heading", { name: "Recovery", exact: true })).toBeVisible();
    expect(
      requests.filter((request) => request.endsWith("/api/v3/operations/submit")),
    ).toHaveLength(1);
  } finally {
    pending.release();
  }
  // This explicit reload is recovery action by the operator, never a language-change side effect.
  await returnToRecovery(page);
  const view = page.getByLabel("Recovery view");
  await expect(view).toBeVisible({ timeout: 10_000 });
  await view.selectOption("TERMINAL");
  await expect(view).toHaveValue("TERMINAL");
  const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Inspect", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
  await expect(page.getByRole("button", { name: "Try to record this request" })).toHaveCount(0);
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
  await inspectionLanguage(page);
  expect(
    await page.getByRole("dialog").evaluate((element, prior) => element === prior, selected),
  ).toBe(true);
  expect(requests).toHaveLength(0);
  await page.getByRole("button", { name: ui("lv", "ui.resolveExact") }).click();
  const pending = await pauseJsonReply(page, "recovery.resolve");
  try {
    await page.getByRole("button", { name: ui("lv", "ui.resolveExact") }).click();
    const captured = await pending.ready;
    const { outcome } = captured.reply;
    expect(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED").toBe(true);
    expect(captured.bytes.equals(Buffer.from(JSON.stringify(identity)))).toBe(true);
    await selectLanguage(page, "ar");
    const confirmation = page.getByRole("dialog", {
      name: ui("ar", "ui.resolveTitle"),
      exact: true,
    });
    await expect(
      confirmation.getByText(ui("ar", "ui.resolveConsequence"), { exact: true }),
    ).toBeVisible();
    await expect(
      confirmation.getByRole("button", { name: ui("ar", "ui.working"), exact: true }),
    ).toBeDisabled();
    await selectFormat(page, "ar-EG");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: ui("ar", "ui.working") })).toBeDisabled();
    await expect(
      page.getByRole("dialog", { name: ui("ar", "ui.resolveTitle"), exact: true }),
    ).toContainText(identity.requestSha256);
    expect(requests).toHaveLength(1);
    await expectAccessible(page);
    pending.release();
    await expect(page.getByRole("status")).toContainText(identity.operationId);
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  } finally {
    pending.release();
  }
});
