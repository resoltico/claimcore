import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase } from "./case-workflow";
import { login, logout, openAuthenticated } from "./session-helpers";
import {
  selectLanguage,
  selectFormat,
  trackRequests,
  ui,
  expectPreferences,
} from "./localization-support";
import { preferenceKey } from "../src/presentation/preferences";

test("updates visible detail and expanded history immediately with independent format and no new reads", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LANGUAGE-HISTORY-${randomUUID()}`;
  const opening = await openCase(page, reference);
  await page.getByRole("combobox", { name: "History detail", exact: true }).selectOption("FULL");
  const history = page
    .locator(".history-list > li > details")
    .filter({ hasText: opening.operationId });
  await expect(history).toHaveCount(1);
  await expect(history).toBeVisible();
  await history.locator(":scope > summary").click();
  await expect(history).toHaveAttribute("open", "");
  await selectFormat(page, "ar-EG");
  const original = await history.elementHandle();
  const requests = trackRequests(page);
  for (const language of ["ar", "en", "lv", "en"] as const) {
    await selectLanguage(page, language);
    await expect(
      page.getByRole("heading", { name: ui(language, "ui.caseDetail"), exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", { name: ui(language, "ui.acceptedHistory"), exact: true }),
    ).toBeVisible();
    await expect(page.getByText(ui(language, "ui.historyHint"), { exact: true })).toBeVisible();
    await expect(
      page.getByRole("heading", { name: ui(language, "ui.continueCase"), exact: true }),
    ).toBeVisible();
    await expect(history).toHaveAttribute("open", "");
    expect(await history.evaluate((element, prior) => element === prior, original)).toBe(true);
    await expectPreferences(page, { displayLocale: "ar-EG" });
    await expect(history.locator('.field-row[data-field-name="claimedAmount"] bdi')).toHaveText(
      "١٬٢٠٠٫٥٠",
    );
  }
  expect(requests).toHaveLength(0);
});

test("preserves language and independent display preferences through real logout and OIDC relogin", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  // This workflow owns the server-side ticket that logout revokes.
  await page.goto("/", { waitUntil: "commit" });
  await login(page);
  await selectFormat(page, "lv-LV");
  await selectLanguage(page, "ar");
  await expect(
    page.getByRole("button", { name: ui("ar", "ui.signOut"), exact: true }),
  ).toBeVisible();
  const logoutResponse = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/session.logout",
  );
  await page.getByRole("button", { name: ui("ar", "ui.signOut"), exact: true }).click();
  expect((await logoutResponse).status()).toBe(200);
  await expect(page.getByRole("link", { name: ui("ar", "ui.signIn"), exact: true })).toBeVisible();
  await expect(page.getByText(ui("ar", "ui.loginInstructions"), { exact: true })).toBeVisible();
  await expectPreferences(page, { displayLocale: "lv-LV" });
  await login(page, ui("ar", "ui.cases"), ui("ar", "ui.signIn"));
  await expectPreferences(page, { language: "ar" });
  await expectPreferences(page, { displayLocale: "lv-LV" });
  await expect(
    page.getByRole("heading", { name: ui("ar", "ui.cases"), exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
      preferenceKey,
    ),
  ).toEqual({ version: 2, language: "ar", displayLocale: "lv-LV", theme: "SYSTEM" });
  await selectLanguage(page, "en");
  await logout(page);
});
