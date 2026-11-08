import { applyAuthority } from "./authority-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { showAccountActions } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase } from "./case-workflow";
import { login, logout, openAuthenticated } from "./session-helpers";
import {
  selectLanguage,
  selectFormat,
  trackForegroundRequests,
  ui,
  expectPreferences,
} from "./localization-support";
import { preferenceKey } from "../src/presentation/preferences";

test("updates supplied records and timeline immediately with independent format and no new reads", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LANGUAGE-HISTORY-${randomUUID()}`;
  const opening = await openCase(page, reference);
  const history = page
    .locator(".case-timeline-panel .table-view tbody > tr")
    .filter({ hasText: opening.operationId });
  await expect(history).toHaveCount(1);
  await expect(history).toBeVisible();
  await selectFormat(page, "ar-EG");
  const original = await history.elementHandle();
  const requests = trackForegroundRequests(page);
  for (const language of ["ar", "en", "lv", "en"] as const) {
    await selectLanguage(page, language);
    await expect(page.getByText("Synthetic claimant", { exact: true }).first()).toBeVisible();
    await expect(
      page.getByRole("heading", { name: ui(language, "ui.timeline.title"), exact: true }),
    ).toBeVisible();

    await expect(
      page
        .locator(".case-actions > summary")
        .filter({ hasText: ui(language, "ui.moreCaseActions") }),
    ).toBeVisible();
    expect(await history.evaluate((element, prior) => element === prior, original)).toBe(true);
    await expectPreferences(page, { displayLocale: "ar-EG" });
    await history.locator("details > summary").click();
    await expect(history.locator("details")).toContainText("١٬٢٠٠٫٥٠");
    await expect(history.locator("details")).toContainText("Synthetic claimant");
    await history.locator("details > summary").click();
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
  await showAccountActions(page);
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
    page.getByRole("heading", { name: ui("ar", "ui.cases"), exact: true, level: 1 }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
      preferenceKey,
    ),
  ).toEqual({
    version: 3,
    language: "ar",
    displayLocale: "lv-LV",
    mode: "system",
    accent: "green" as const,
  });
  await selectLanguage(page, "en");
  await logout(page);
});

test("keeps exact old and new facts in the real timeline view", async ({ page }) => {
  await openAuthenticated(page);
  await selectFormat(page, "en-GB");
  const reference = `EXACT-HISTORY-${randomUUID()}`;
  await openCase(page, reference);
  await applyAuthority(page, {
    kind: "CASE",
    operationId: mintOperationId(),
    target: { kind: "REFERENCE", caseReference: reference },
    expectedRevision: 1,
    act: {
      kind: "AMEND_REGISTRATION",
      values: { incidentDescription: "Earlier browser history\nOriginal exact paragraph" },
    },
  });
  await applyAuthority(page, {
    kind: "CASE",
    operationId: mintOperationId(),
    target: { kind: "REFERENCE", caseReference: reference },
    expectedRevision: 2,
    act: {
      kind: "AMEND_REGISTRATION",
      values: {
        claimed: { amount: "9876.5432", currency: "EUR" },
        incidentDate: "2026-09-07",
        incidentNotificationDate: "2026-09-08",
        incidentDescription: "Distinctive browser history\nSecond exact paragraph",
      },
    },
  });
  await page.reload();
  const row = page.locator(".case-timeline-panel tbody > tr").filter({ hasText: "9,876.5432 EUR" });
  await expect(row).toHaveCount(1);
  await row.locator("details > summary").click();
  await expect(row.locator("details")).toContainText("1,200.50 EUR");
  await expect(row.locator("details")).toContainText("9,876.5432 EUR");
  await expect(row.locator("details")).toContainText("01/09/2026");
  await expect(row.locator("details")).toContainText("07/09/2026");
  expect(
    await row
      .locator("details")
      .locator('[data-section="field.incidentDescription.label"] [data-entry$=".before"] bdi')
      .textContent(),
  ).toBe("Earlier browser history\nOriginal exact paragraph");
  expect(
    await row
      .locator("details")
      .locator('[data-section="field.incidentDescription.label"] [data-entry$=".after"] bdi')
      .textContent(),
  ).toBe("Distinctive browser history\nSecond exact paragraph");
});
