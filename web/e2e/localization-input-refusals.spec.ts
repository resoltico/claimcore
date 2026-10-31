import { fillBusinessField } from "./typed-entry";
import { expectInputRefusal, issueMeanings } from "./input-refusal-expectations";
import { isResponse } from "../src/protocol/client";
import type { Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, startOpen, startAct } from "./case-workflow";
import { openAuthenticated, browserRequest, sessionToken, installationId } from "./session-helpers";
import {
  confirmReview,
  pauseJsonReply,
  businessFormatRefusalText,
  expectBusinessFormatRefusal,
  trackRequests,
  selectLanguage,
  selectFormat,
  ui,
} from "./localization-support";

const forgedCorrection = async (page: Page, reference: string) => {
  const antiforgery = await sessionToken(page);
  const reply = await browserRequest(page, "/api/operation.execute", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": antiforgery,
      "ClaimCore-Installation": await installationId(page, antiforgery),
    },
    body: JSON.stringify({
      kind: "CASE",
      operationId: randomUUID(),
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: 2,
      act: {
        kind: "CORRECT",
        groups: {
          registration: { kind: "KEEP" },
          decision: { kind: "CLEAR" },
          payment: { kind: "KEEP" },
        },
      },
    }),
  });
  expect(reply.status).toBe(200);
  if (!isResponse("operation.execute", reply.payload)) {
    throw new Error("Expected typed correction refusal.");
  }
  const { outcome } = reply.payload;
  expect(outcome.kind).toBe("REJECTED");
  if (outcome.kind === "REJECTED") {
    expect(outcome.data.code).toBe("CORRECTION_REQUIRES_EXISTING_VALUE");
  }
};
const reviewDecimal = async (page: Page, language: "en" | "lv" | "ar") => {
  const samples = {
    en: ["en-GB", "0.10"],
    lv: ["lv-LV", "0,10"],
    ar: ["ar-EG", "٠٫١٠"],
  } as const;
  const [locale, amount] = samples[language];
  await selectFormat(page, locale);
  await fillBusinessField(page, "claimedAmount", amount);
  const sent = page.waitForRequest((request) => request.url().endsWith("/api/operation.preview"));
  const pending = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.preview"),
  );
  await page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }).click();
  const draft: unknown = (await sent).postDataJSON();
  expect(draft).toMatchObject({ act: { kind: "OPEN", values: { claimedAmount: "0.10" } } });
  const value: unknown = await (await pending).json();
  expect(isResponse("operation.preview", value)).toBe(true);
  if (!isResponse("operation.preview", value)) {
    throw new Error("Expected typed review.");
  }
  expect(value.outcome.kind).toBe("REVIEWED");
};

for (const language of ["en", "lv", "ar"] as const) {
  test(`normalizes declared locale amounts and refuses malformed syntax in ${language}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await startOpen(page, `SYNTAX-${randomUUID()}`);
    await selectLanguage(page, language);
    const requests = trackRequests(page);
    for (const [name, value, diagnostic] of [
      ["claimedAmount", "00.10", "DECIMAL_FORMAT"],
      ["incidentDate", "", "CALENDAR_DATE_REQUIRED"],
      ["claimedCurrency", "ĀBC", "CURRENCY_FORMAT"],
    ] as const) {
      await fillBusinessField(page, name, value);
      const field = page.locator(`[data-field-name="${name}"]`);
      const control = name === "incidentDate" ? field.getByRole("spinbutton").first() : field;
      const before = requests.length;
      await expectBusinessFormatRefusal(page, name, diagnostic, () =>
        page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }).click(),
      );
      await expectInputRefusal(page, control, language, {
        meaning: issueMeanings[language][name],
        issue: businessFormatRefusalText[diagnostic][language],
      });
      await expect(page.locator("[data-error-summary]")).toBeFocused();
      await page.locator("[data-error-summary]").getByRole("link").click();
      await expect(control).toBeFocused();
      await expect(control).toHaveAttribute("aria-invalid", "true");
      expect(requests.slice(before)).toEqual(["POST /api/operation.preview"]);
      await expect(
        name === "incidentDate" ? field.locator('input[type="hidden"]') : control,
      ).toHaveValue(value);
      await expect(page.locator("section.receipt")).toHaveCount(0);
      await page.locator('input[data-field-name="claimedAmount"]').fill("1200.50");
      await fillBusinessField(page, "incidentDate", "2026-09-01");
      await fillBusinessField(page, "claimedCurrency", "EUR");
    }
    await reviewDecimal(page, language);
  });
  test(`offers KEEP for missing recorded groups and refuses forged correction in ${language}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const reference = `MISSING-${randomUUID()}`;
    await openCase(page, reference);
    await startAct(page, "Close the case");
    // Close through the existing native workflow, then try to correct an absent decision.
    const first = await pauseJsonReply(page, "operation.preview");
    await page.getByRole("button", { name: "Review changes" }).click();
    await first.ready;
    first.release();
    await confirmReview(page);
    await page.getByRole("button", { name: "Record changes" }).click();
    await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
    await expect(page.locator(".success-toast")).toContainText(
      "Close the case recorded at revision 2.",
    );
    await startAct(page, "Correct case facts");
    await selectLanguage(page, language);
    const mode = page.locator('input[type="radio"][name="act.groups.decision.kind"]');
    await expect(mode).toHaveCount(1);
    await expect(mode).toHaveValue("KEEP");
    await expect(mode).toBeChecked();
    await forgedCorrection(page, reference);
    await expect(mode).toHaveValue("KEEP");
    await expect(mode).toBeChecked();
  });
}
