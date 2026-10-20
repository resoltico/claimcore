import { isResponse } from "../src/protocol/client";
import type { Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, startOpen, startCommand } from "./case-workflow";
import { openAuthenticated, browserRequest, sessionToken } from "./session-helpers";
import {
  confirmReview,
  pauseJsonReply,
  businessFormatRefusalText,
  expectBusinessFormatRefusal,
  trackRequests,
  selectLanguage,
  ui,
} from "./localization-support";

const forgedCorrection = async (page: Page, reference: string) => {
  const reply = await browserRequest(page, "/api/case.execute", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      operationId: randomUUID(),
      caseReference: reference,
      expectedRevision: 2,
      act: {
        kind: "CORRECT_CASE",
        groups: {
          registration: { mode: "KEEP" },
          decision: { mode: "CLEAR" },
          payment: { mode: "KEEP" },
        },
      },
    }),
  });
  expect(reply.status).toBe(200);
  if (!isResponse("case.execute", reply.payload)) {
    throw new Error("Expected typed correction refusal.");
  }
  const { outcome } = reply.payload;
  expect(outcome.tag).toBe("REJECTED");
  if (outcome.tag === "REJECTED") {
    expect(outcome.data.diagnostic.id).toBe("CORRECTION_EXISTING_VALUE_REQUIRED");
  }
};
const reviewDecimal = async (page: Page, language: "en" | "lv" | "ar") => {
  await page.locator('input[name="claimedAmount"]').fill("0.10");
  const pending = page.waitForResponse((response) => response.url().endsWith("/api/case.preview"));
  await page
    .getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true })
    .click();
  const value: unknown = await (await pending).json();
  expect(isResponse("case.preview", value)).toBe(true);
  if (!isResponse("case.preview", value)) {
    throw new Error("Expected typed review.");
  }
  expect(value.outcome.tag).toBe("REVIEWED");
};

for (const language of ["en", "lv", "ar"] as const) {
  test(`refuses localized authoring digits and currency alphabets in ${language} [CC-DOM-001]`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await startOpen(page, `SYNTAX-${randomUUID()}`);
    await selectLanguage(page, language);
    const requests = trackRequests(page);
    for (const [name, value, diagnostic] of [
      ["claimedAmount", "١٢٫٥٠", "INPUT_DECIMAL_FORMAT"],
      ["claimedAmount", "125,50", "INPUT_DECIMAL_FORMAT"],
      ["claimedAmount", "00.10", "INPUT_DECIMAL_FORMAT"],
      ["incidentDate", "٢٠٢٦-٠٩-٠١", "INPUT_CALENDAR_DATE_REQUIRED"],
      ["claimedCurrency", "ĀBC", "INPUT_CURRENCY_FORMAT"],
    ] as const) {
      await page.locator(`input[name="${name}"]`).fill(value);
      const before = requests.length;
      await expectBusinessFormatRefusal(page, name, diagnostic, () =>
        page
          .getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true })
          .click(),
      );
      await expect(page.getByRole("alert")).toContainText(
        businessFormatRefusalText[diagnostic][language],
      );
      await expect(page.locator(`input[name="${name}"]`)).toBeFocused();
      await expect(page.locator(`input[name="${name}"]`)).toHaveAttribute("aria-invalid", "true");
      expect(requests.slice(before)).toEqual(["POST /api/case.preview"]);
      await expect(page.locator(`input[name="${name}"]`)).toHaveValue(value);
      await expect(page.locator("section.receipt")).toHaveCount(0);
      await page.locator('input[name="claimedAmount"]').fill("1200.50");
      await page.locator('input[name="incidentDate"]').fill("2026-09-01");
      await page.locator('input[name="claimedCurrency"]').fill("EUR");
    }
    await reviewDecimal(page, language);
  });
  test(`offers KEEP for missing recorded groups and refuses forged correction in ${language} [CC-DOM-002]`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const reference = `MISSING-${randomUUID()}`;
    await openCase(page, reference);
    await startCommand(page, "Close the case");
    // Close through the existing native workflow, then try to correct an absent decision.
    const first = await pauseJsonReply(page, "case.preview");
    await page.getByRole("button", { name: "Review changes" }).click();
    await first.ready;
    first.release();
    await confirmReview(page);
    await page.getByRole("button", { name: "Record changes" }).click();
    await page.getByRole("button", { name: "Return to case" }).click();
    await startCommand(page, "Correct case facts");
    await selectLanguage(page, language);
    const mode = page.locator("#correction-decision-mode");
    await expect(mode).toHaveValue("KEEP");
    await expect(mode.locator("option")).toHaveCount(1);
    await forgedCorrection(page, reference);
    await expect(mode).toHaveValue("KEEP");
  });
}
