import { type RequestByEndpoint } from "@contracts/client";
import { applyPeople } from "./people-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { showAccountActions } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openCase } from "./case-workflow";
import { login, logout, openAuthenticated, signOut } from "./session-helpers";
import {
  selectLanguage,
  selectFormat,
  trackForegroundRequests,
  ui,
  expectPreferences,
} from "./localization-support";
import { preferenceKey } from "../src/presentation/preferences";

// The provider navigation and logout reply carry an ID token hint.
test.use({ trace: "off" });

test("updates supplied records without refetching while receipt disclosure refreshes after Settings", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LANGUAGE-HISTORY-${randomUUID()}`;
  const opening = await openCase(page, reference);
  const history = page.locator(
    `.case-timeline-panel .region-timeline > li[data-effect$=".${opening.operationId}"]`,
  );
  await expect(history).toHaveCount(1);
  await expect(history).toBeVisible();
  await selectFormat(page, "ar-EG");
  const original = await history.elementHandle();
  const requests = trackForegroundRequests(page);
  const receiptReads: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.get") {
      const operationGetRequest = request.postDataJSON() as RequestByEndpoint["operation.get"];
      receiptReads.push(operationGetRequest.operationId);
    }
  });
  for (const language of ["ar", "en", "lv", "en"] as const) {
    await selectLanguage(page, language);
    await expect(
      page
        .getByText("Synthetic claimant", {
          exact: true,
        })
        .first(),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", {
        name: ui(language, "ui.timeline.title"),
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      page.locator(".case-header-actions button[aria-haspopup]").filter({
        hasText: ui(language, "ui.moreCaseActions"),
      }),
    ).toBeVisible();
    expect(await history.evaluate((element, prior) => element === prior, original)).toBe(true);
    await expectPreferences(page, {
      displayLocale: "ar-EG",
    });
    await history
      .getByRole("button", { name: ui(language, "ui.viewDetails"), exact: true })
      .click();
    const details = page.getByRole("dialog", { name: ui(language, "ui.viewDetails"), exact: true });
    await expect(details).toContainText("١٬٢٠٠٫٥٠");
    await expect(
      details.locator('[data-change-label="field.claimedAmount.label"] [data-side="after"]'),
    ).toContainText("١٬٢٠٠٫٥٠");
    await details
      .getByRole("button", { name: ui(language, "ui.close"), exact: true })
      .first()
      .click();
  }
  expect(requests.filter((request) => request !== "POST /api/operation.get")).toEqual([]);
  expect(receiptReads.length).toBeGreaterThan(0);
  expect(new Set(receiptReads)).toEqual(new Set([opening.operationId]));
});
test("preserves language and independent display preferences through real logout and OIDC relogin", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout * 2);
  // This workflow owns the server-side ticket that logout revokes.
  await page.goto("/", {
    waitUntil: "commit",
  });
  await login(page);
  await selectFormat(page, "lv-LV");
  await selectLanguage(page, "ar");
  await showAccountActions(page);
  const control = page.getByRole("menuitem", {
    name: ui("ar", "ui.signOut"),
    exact: true,
  });
  await signOut(page, control);
  await expect(
    page.getByRole("link", {
      name: ui("ar", "label.signInAgain"),
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByText(ui("ar", "notice.signedOutTitle"), {
      exact: true,
    }),
  ).toBeVisible();
  await expectPreferences(page, {
    displayLocale: "lv-LV",
  });
  await login(page, ui("ar", "label.cases"), ui("ar", "ui.signIn"));
  await expectPreferences(page, {
    language: "ar",
  });
  await expectPreferences(page, {
    displayLocale: "lv-LV",
  });
  await expect(
    page.getByRole("heading", {
      name: ui("ar", "label.cases"),
      exact: true,
      level: 1,
    }),
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
    accent: "blue" as const,
  });
  await selectLanguage(page, "en");
  await logout(page);
});
test("keeps exact old and new facts in the real timeline view", async ({ page }) => {
  await openAuthenticated(page);
  await selectFormat(page, "en-GB");
  const reference = `EXACT-HISTORY-${randomUUID()}`;
  await openCase(page, reference);
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "case.amendRegistration",
    binding: {
      kind: "CASE",
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: 1,
    },
    inputs: { "field:incidentDescription": "Earlier browser history\nOriginal exact paragraph" },
  });
  const edit = mintOperationId();
  await applyPeople(page, {
    operationId: edit,
    task: "case.amendRegistration",
    binding: {
      kind: "CASE",
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: 2,
    },
    inputs: {
      "field:claimedAmount": "9876.5432",
      "field:claimedCurrency": "EUR",
      "field:incidentDate": "2026-09-07",
      "field:receivedDate": "2026-09-08",
      "field:incidentDescription": "Distinctive browser history\nSecond exact paragraph",
    },
  });
  await page.reload();
  const event = page.locator(`.case-timeline-panel .region-timeline > li[data-effect$=".${edit}"]`);
  await expect(event).toHaveCount(1);
  await event.getByRole("button", { name: "View details", exact: true }).click();
  const row = page.getByRole("dialog", { name: "View details", exact: true });
  await expect(row).toContainText("1,200.50 EUR");
  await expect(row).toContainText("9,876.5432 EUR");
  await expect(row).toContainText("01/09/2026");
  await expect(row).toContainText("07/09/2026");
  expect(
    await row
      .locator('[data-change-label="field.incidentDescription.label"] [data-side="before"] bdi')
      .textContent(),
  ).toBe("Earlier browser history\nOriginal exact paragraph");
  expect(
    await row
      .locator('[data-change-label="field.incidentDescription.label"] [data-side="after"] bdi')
      .textContent(),
  ).toBe("Distinctive browser history\nSecond exact paragraph");
});
