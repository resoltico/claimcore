import { registration } from "../tests/support/inputs";
import { fillBusinessField } from "./typed-entry";
import { expectInputRefusal } from "./input-refusal-expectations";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { startOpen } from "./case-workflow";
import {
  expectAccessible,
  openAuthenticated,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import {
  expectKeyboardContained,
  confirmReview,
  pauseJsonReply,
  expectBusinessFormatRefusal,
  reviewFrom,
  selectFormat,
  requestLanguage,
  selectLanguage,
  trackForegroundRequests,
  ui,
} from "./localization-support";

// Literal GUI-copy oracles distinguish widget hints, optional wire rules and diagnostic registers.
const widgetHints = {
  en: {
    incidentDate: "When the incident happened.",
    claimantName: "The person or company making the claim.",
  },
  lv: {
    incidentDate: "Kad notika negadījums.",
    claimantName: "Persona vai uzņēmums, kas iesniedz prasījumu.",
  },
  ar: { incidentDate: "متى وقعت الواقعة.", claimantName: "الشخص أو الشركة التي تقدم المطالبة." },
} as const;
const calendarGuiIssue = {
  en: "Enter a real date.",
  lv: "Ievadiet derīgu datumu.",
  ar: "أدخل تاريخًا صحيحًا.",
} as const;
const calendarRules = {
  en: "Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06.",
  lv: "Izmantojiet ASCII ciparus formātā YYYY-MM-DD, piemēram, 2026-10-06.",
  ar: "استخدم أرقام ASCII بالتنسيق YYYY-MM-DD، مثل 2026-10-06.",
} as const;

const unacceptedCase = async (page: Page, reference: string) => {
  const reply = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ target: { kind: "REFERENCE", caseReference: reference } }),
  });
  if (reply.status !== 200 || !isResponse("case.get", reply.payload)) {
    throw new Error("E2E_UNACCEPTED_CASE_READ_INVALID");
  }
  expect(reply.payload.outcome.kind).toBe("REJECTED");
  if (reply.payload.outcome.kind === "REJECTED") {
    expect(reply.payload.outcome.data.code).toBe("RESOURCE_UNAVAILABLE");
  }
};
// Segmented dates cannot author malformed text; the API still has to reject it.
const malformedCalendarBoundary = async (page: Page, reference: string) => {
  for (const incidentDate of ["2026-02-30", "٢٠٢٦-٠٩-٠١"]) {
    const reply = await browserRequest(page, "/api/operation.preview", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: JSON.stringify({
        kind: "CASE",
        operationId: randomUUID(),
        target: { kind: "REFERENCE", caseReference: reference },
        expectedRevision: 0,
        act: { kind: "OPEN", values: registration({ incidentDate }) },
      }),
    });
    expect(reply.status).toBe(200);
    if (
      !isResponse("operation.preview", reply.payload) ||
      reply.payload.outcome.kind !== "REJECTED"
    ) {
      throw new Error("Expected malformed calendar refusal.");
    }
    expect(reply.payload.outcome.data).toEqual({
      code: "INVALID_INPUT",
      action: "CORRECT_INPUT",
      issues: [{ field: "incidentDate", code: "CALENDAR_DATE_REQUIRED" }],
    });
  }
};
const invalidCalendar = async (page: Page, requests: string[], language: "en" | "lv" | "ar") => {
  await expectBusinessFormatRefusal(page, "incidentDate", "CALENDAR_DATE_REQUIRED", () =>
    page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }).click(),
  );
  await expect(page.locator("[data-error-summary]")).toBeFocused();
  await page.locator("[data-error-summary]").getByRole("link").click();
  await expect(
    page.locator('[data-field-name="incidentDate"]').getByRole("spinbutton").first(),
  ).toBeFocused();
  await expect(
    page.locator('[data-field-name="incidentDate"]').getByRole("spinbutton").first(),
  ).toHaveAttribute("aria-invalid", "true");
  await expectInputRefusal(
    page,
    page.locator('[data-field-name="incidentDate"]').getByRole("spinbutton").first(),
    language,
    {
      meaning: widgetHints[language].incidentDate,
      issue: calendarGuiIssue[language],
    },
  );
  const date = page.locator('[data-field-name="incidentDate"]');
  await expect(date.getByRole("spinbutton").first()).not.toHaveAccessibleDescription(/ASCII|YYYY/u);
  // The precise rule is available through its actual info tip, not duplicated in the ordinary hint.
  await date.locator(".field-help > summary").click();
  await expect(date.locator(".field-help p")).toHaveText(calendarRules[language]);
  await page.keyboard.press("Escape");
  await expect(date.locator(".field-help")).not.toHaveAttribute("open");
  await expect(date.locator('input[type="hidden"]')).toHaveValue("");
  await expect(page.locator('input[data-field-name="claimantName"]')).toHaveValue(
    "A\u0308 / العربية",
  );
  expect(requests).toEqual(["POST /api/operation.preview"]);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expectAccessible(page);
};
const policyRefusal = async (page: Page, requests: string[], language: "en" | "lv" | "ar") => {
  const name = page.locator('input[data-field-name="claimantName"]');
  const authored = `A\u0308 / العربية${"x".repeat(201)}`;
  await fillBusinessField(page, "incidentDate", "2026-09-01");
  await name.fill(authored);
  const pending = await pauseJsonReply(page, "operation.preview");
  try {
    await page
      .getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true })
      .click();
    const { outcome } = (await pending.ready).reply;
    expect(outcome.kind).toBe("REJECTED");
    if (outcome.kind === "REJECTED") {
      expect(outcome.data).toEqual({
        code: "INVALID_INPUT",
        action: "CORRECT_INPUT",
        issues: [{ field: "claimantName", code: "TOO_LONG", maximumCharacters: 200 }],
      });
    }
    pending.release();
    await expect(page.locator("[data-error-summary]")).toBeFocused();
    await page.locator("[data-error-summary]").getByRole("link").click();
    await expect(name).toBeFocused();
    await expect(name).toHaveAttribute("aria-invalid", "true");
    const policyText = {
      lv: "Vērtība nedrīkst pārsniegt 200 Unicode rakstzīmes.",
      ar: "يجب ألا تتجاوز القيمة \u2068200\u2069 محارف Unicode.",
      en: "The value must not exceed 200 Unicode characters.",
    };
    await expectInputRefusal(page, name, language, {
      meaning: widgetHints[language].claimantName,
      issue: policyText[language],
    });
    await expect(name).toHaveAttribute("aria-invalid", "true");
    await expect(name).toHaveValue(authored);
    expect(requests).toEqual(["POST /api/operation.preview", "POST /api/operation.preview"]);
  } finally {
    pending.release();
  }
};

for (const language of ["en", "lv", "ar"] as const) {
  test(`preserves authored Unicode and cleared calendar input with real ${language} policy refusals`, async ({
    page,
  }) => {
    const reference = `LOCALE-VALIDATE-${randomUUID()}`;
    await openAuthenticated(page);
    await selectLanguage(page, language);
    await startOpen(page, reference, ui(language, "act.OPEN.label"));
    await malformedCalendarBoundary(page, reference);
    const name = page.locator('input[data-field-name="claimantName"]');
    await name.fill("A\u0308 / العربية");
    await fillBusinessField(page, "incidentDate", "");
    await name.evaluate((element: HTMLInputElement) => {
      element.setSelectionRange(1, 3);
    });
    const originalNode = await name.elementHandle();
    const requests = trackForegroundRequests(page);
    await invalidCalendar(page, requests, language);
    expect(await name.evaluate((element, prior) => element === prior, originalNode)).toBe(true);
    await policyRefusal(page, requests, language);
    await expectAccessible(page);
    await unacceptedCase(page, reference);
    expect(requests.filter((request) => request === "POST /api/operation.execute")).toEqual([]);
  });
}

test("an in-flight language activation preserves an open review awaiting explicit consent and frozen bytes", async ({
  page,
}) => {
  await openAuthenticated(page);
  await selectFormat(page, "ar-EG");
  const languageReady = Promise.withResolvers<void>();
  const languageRelease = Promise.withResolvers<void>();
  const languageAsset = /\/assets\/language-lv-[^/]+\.js$/u;
  await page.route(languageAsset, async (route) => {
    languageReady.resolve();
    await languageRelease.promise;
    await route.continue();
  });
  let releasePreview: (() => void) | null = null;
  try {
    await requestLanguage(page, "lv");
    await languageReady.promise;
    await startOpen(page, `LOCALE-REVIEW-${randomUUID()}`);
    const requests = trackForegroundRequests(page);
    const pending = await pauseJsonReply(page, "operation.preview");
    releasePreview = pending.release;
    const review = page.getByRole("button", { name: "Review changes", exact: true });
    await review.click();
    const captured = await pending.ready;
    const identity = reviewFrom(captured.reply, captured.bytes);
    pending.release();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    const originalDialog = await dialog.elementHandle();
    const confirmation = dialog.locator("button[data-confirm-operation]");
    await expect(confirmation).toHaveAccessibleName("Open a case");
    await expect(dialog.getByRole("checkbox")).toHaveCount(0);
    await confirmReview(page);
    await expectKeyboardContained(page, dialog);
    await expect(dialog.locator(".settings-panel")).toHaveCount(0);
    languageRelease.resolve();
    await expect(page.locator("html")).toHaveAttribute("lang", "lv");
    expect(await dialog.evaluate((element, prior) => element === prior, originalDialog)).toBe(true);
    await expect(confirmation).toBeEnabled();
    await expect(confirmation).toHaveAccessibleName(ui("lv", "act.OPEN.label"));
    await expect(dialog).toContainText(identity.operationId);
    expect(requests).toEqual(["POST /api/operation.preview"]);
    await expectAccessible(page);
    await dialog.getByRole("button", { name: ui("lv", "ui.returnToEdit"), exact: true }).click();
    await expect(dialog.locator('button[type="submit"]')).toBeFocused();
  } finally {
    languageRelease.resolve();
    releasePreview?.();
    await page.unroute(languageAsset);
  }
});
