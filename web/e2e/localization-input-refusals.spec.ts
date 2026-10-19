import { isResponse } from "../src/protocol/client";
import type { Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, startOpen, startCommand } from "./case-workflow";
import { openAuthenticated, browserRequest, sessionToken } from "./session-helpers";
import {
  confirmReview,
  pauseJsonReply,
  localInputRefusalText,
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
for (const language of ["en", "lv", "ar"] as const) {
  test(`refuses localized authoring digits and currency alphabets in ${language} [CC-DOM-001]`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await startOpen(page, `SYNTAX-${randomUUID()}`);
    await selectLanguage(page, language);
    const requests = trackRequests(page);
    for (const [name, value] of [
      ["claimedAmount", "١٢٫٥٠"],
      ["claimedAmount", "125,50"],
      ["incidentDate", "٢٠٢٦-٠٩-٠١"],
      ["claimedCurrency", "ĀBC"],
    ]) {
      await page.locator(`input[name="${name}"]`).fill(value!);
      await page
        .getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true })
        .click();
      await expect(page.getByRole("alert")).toContainText(localInputRefusalText[language]);
      await expect(page.locator(`input[name="${name}"]`)).toBeFocused();
      expect(requests).toHaveLength(0);
      await expect(page.locator(`input[name="${name}"]`)).toHaveValue(value!);
      await expect(page.locator("section.receipt")).toHaveCount(0);
      await page.locator('input[name="claimedAmount"]').fill("1200.50");
      await page.locator('input[name="incidentDate"]').fill("2026-09-01");
      await page.locator('input[name="claimedCurrency"]').fill("EUR");
    }
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
