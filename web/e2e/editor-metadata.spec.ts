import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { openCase, reviewDraft, startAct } from "./case-workflow";
import { expectAccessible, openAuthenticated, progress } from "./session-helpers";

test("prefills routed metadata and confirms discard before changing acts from the case", async ({
  page,
}) => {
  const caseReference = `DESCRIPTORS-${randomUUID()}`;
  await openAuthenticated(page);
  await openCase(page, caseReference);
  await startAct(page, "Amend registration");
  await progress("metadata-amend-editor");
  await page.locator('input[data-selects-fields~="claimantName"]').check();
  const claimant = page.getByLabel("Claimant name", { exact: true });
  await expect(claimant).toHaveValue("Synthetic claimant");
  await claimant.fill("Synthetic amended claimant");
  await progress("metadata-dirty-change");
  await page.getByRole("button", { name: "Back to case", exact: true }).click();
  const discard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expectAccessible(page);
  await discard.getByRole("button", { name: "Keep editing" }).click();
  await progress("metadata-kept-editing");
  await expect(claimant).toHaveValue("Synthetic amended claimant");
  await page.getByRole("button", { name: "Back to case", exact: true }).click();
  await discard.getByRole("button", { name: "Discard and leave" }).click();
  await startAct(page, "Record payment decision");
  await progress("metadata-discarded");
  await expect(
    page.locator('[data-field-name="paymentDecisionDate"] input[type="hidden"]'),
  ).toHaveValue("");
  await reviewDraft(page, {
    "Payment decision date": "2026-09-03",
    "Amount to be paid": "300.25",
    "Currency of amount to be paid": "EUR",
  });
  const review = page.getByRole("dialog", { name: "Review changes" });
  await expect(review).toContainText("Payment decision date");
  await expect(review).toContainText("300.25");
  await expectAccessible(page);
});
