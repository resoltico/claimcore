import { expectInputRefusal, issueMeanings } from "./input-refusal-expectations";
import { isResponse } from "../src/protocol/client";
import type { Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, startOpen, startAct } from "./case-workflow";
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
  const reply = await browserRequest(page, "/api/operation.execute", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      kind: "CASE",
      operationId: randomUUID(),
      caseReference: reference,
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
  await page.locator('input[data-field-name="claimedAmount"]').fill("0.10");
  const pending = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.preview"),
  );
  await page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }).click();
  const value: unknown = await (await pending).json();
  expect(isResponse("operation.preview", value)).toBe(true);
  if (!isResponse("operation.preview", value)) {
    throw new Error("Expected typed review.");
  }
  expect(value.outcome.kind).toBe("REVIEWED");
};

for (const language of ["en", "lv", "ar"] as const) {
  test(`refuses localized authoring digits and currency alphabets in ${language}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await startOpen(page, `SYNTAX-${randomUUID()}`);
    await selectLanguage(page, language);
    const requests = trackRequests(page);
    for (const [name, value, diagnostic] of [
      ["claimedAmount", "١٢٫٥٠", "DECIMAL_FORMAT"],
      ["claimedAmount", "125,50", "DECIMAL_FORMAT"],
      ["claimedAmount", "00.10", "DECIMAL_FORMAT"],
      ["incidentDate", "٢٠٢٦-٠٩-٠١", "CALENDAR_DATE_REQUIRED"],
      ["claimedCurrency", "ĀBC", "CURRENCY_FORMAT"],
    ] as const) {
      await page.locator(`input[data-field-name="${name}"]`).fill(value);
      const before = requests.length;
      await expectBusinessFormatRefusal(page, name, diagnostic, () =>
        page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }).click(),
      );
      await expectInputRefusal(page, page.locator(`input[data-field-name="${name}"]`), language, {
        meaning: issueMeanings[language][name],
        issue: businessFormatRefusalText[diagnostic][language],
      });
      await expect(page.locator(`input[data-field-name="${name}"]`)).toBeFocused();
      await expect(page.locator(`input[data-field-name="${name}"]`)).toHaveAttribute(
        "aria-invalid",
        "true",
      );
      expect(requests.slice(before)).toEqual(["POST /api/operation.preview"]);
      await expect(page.locator(`input[data-field-name="${name}"]`)).toHaveValue(value);
      await expect(page.locator("section.receipt")).toHaveCount(0);
      await page.locator('input[data-field-name="claimedAmount"]').fill("1200.50");
      await page.locator('input[data-field-name="incidentDate"]').fill("2026-09-01");
      await page.locator('input[data-field-name="claimedCurrency"]').fill("EUR");
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
    await page.getByRole("button", { name: "Return to case" }).click();
    await startAct(page, "Correct case facts");
    await selectLanguage(page, language);
    const mode = page.locator('[name="act.groups.decision.kind"]');
    await expect(mode).toHaveValue("KEEP");
    await expect(mode.locator("option")).toHaveCount(1);
    await forgedCorrection(page, reference);
    await expect(mode).toHaveValue("KEEP");
  });
}
