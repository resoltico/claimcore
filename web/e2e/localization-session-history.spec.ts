import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase } from "./case-workflow";
import { login, logout, openAuthenticated } from "./session-helpers";
import { selectLanguage, selectFormat, trackRequests, ui } from "./localization-support";
import { preferenceKey } from "../src/presentation/preferences";

test("updates visible detail and expanded history immediately with independent format and no new reads", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LANGUAGE-HISTORY-${randomUUID()}`;
  await openCase(page, reference);
  const history = page.locator(".history-list details").first();
  await expect(history).toBeVisible();
  await history.locator("summary").click();
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
      page.getByRole("heading", { name: ui(language, "ui.availableCommands"), exact: true }),
    ).toBeVisible();
    await expect(history).toHaveAttribute("open", "");
    expect(await history.evaluate((element, prior) => element === prior, original)).toBe(true);
    await expect(page.locator('select[id$="-format"]')).toHaveValue("ar-EG");
    await expect(history.locator('.field-row[data-field-name="claimedAmount"] bdi')).toHaveText(
      "١٬٢٠٠٫٥",
    );
  }
  expect(requests).toHaveLength(0);
});

test("preserves language and independent display preferences through real logout and OIDC relogin", async ({
  page,
}) => {
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await login(page);
  await selectFormat(page, "lv-LV");
  await selectLanguage(page, "ar");
  await expect(
    page.getByRole("button", { name: ui("ar", "ui.signOut"), exact: true }),
  ).toBeVisible();
  const logoutResponse = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/v3/session/logout",
  );
  await page.getByRole("button", { name: ui("ar", "ui.signOut"), exact: true }).click();
  expect((await logoutResponse).status()).toBe(200);
  await expect(page.getByRole("link", { name: ui("ar", "ui.signIn"), exact: true })).toBeVisible();
  await expect(page.getByText(ui("ar", "ui.loginInstructions"), { exact: true })).toBeVisible();
  await expect(page.locator('select[id$="-format"]')).toHaveValue("lv-LV");
  await login(page, ui("ar", "ui.cases"), ui("ar", "ui.signIn"));
  await expect(page.locator('select[id$="-language"]')).toHaveValue("ar");
  await expect(page.locator('select[id$="-format"]')).toHaveValue("lv-LV");
  await expect(
    page.getByRole("heading", { name: ui("ar", "ui.cases"), exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
      preferenceKey,
    ),
  ).toEqual({ version: 1, language: "ar", displayLocale: "lv-LV" });
  await selectLanguage(page, "en");
  await logout(page);
});
