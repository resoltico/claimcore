import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { openCase, prepare, startCommand } from "./case-workflow";
import { expectAccessible, openAuthenticated, progress } from "./session-helpers";

test("prefills metadata fields and confirms dirty command changes before review", async ({
  page,
}) => {
  const caseReference = `DESCRIPTORS-${randomUUID()}`;
  await openAuthenticated(page);
  await openCase(page, caseReference);
  await startCommand(page, "Amend registration");
  await progress("metadata-amend-editor");
  const claimant = page.getByLabel("Claimant name");
  await expect(claimant).toHaveValue("Synthetic claimant");
  await claimant.fill("Synthetic amended claimant");
  await progress("metadata-dirty-change");
  await page.getByLabel("Command", { exact: true }).selectOption("DECIDE");
  const discard = page.getByRole("dialog", { name: "Discard this command draft?" });
  await expectAccessible(page);
  await discard.getByRole("button", { name: "Keep editing" }).click();
  await progress("metadata-kept-editing");
  await expect(claimant).toHaveValue("Synthetic amended claimant");
  await page.getByLabel("Command", { exact: true }).selectOption("DECIDE");
  await discard.getByRole("button", { name: "Discard and change command" }).click();
  await progress("metadata-discarded");
  await expect(page.getByLabel("Payment decision date")).toHaveValue("");
  await prepare(page, {
    "Payment decision date": "2026-09-03",
    "Amount to be paid": "300.25",
    "Currency of amount to be paid": "EUR",
  });
  const review = page.getByRole("dialog", { name: "Review prepared operation" });
  await expect(review).toContainText("Payment decision date");
  await expect(review).toContainText("300.25");
  await expectAccessible(page);
});
