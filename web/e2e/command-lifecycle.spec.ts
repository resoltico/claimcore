import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { completeCommand, openCase } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

const reference = (): string => `BROWSER-${randomUUID()}`;

const decide = async (page: import("@playwright/test").Page): Promise<void> => {
  await completeCommand(page, "Record payment decision", {
    "Payment decision date": "2026-09-03",
    "Amount to be paid": "300.25",
    "Currency of amount to be paid": "EUR",
  });
};

test("publishes open, amendment, decision, withdrawal, full history and operation lookup", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  const opened = await openCase(page, caseReference);
  await completeCommand(page, "Amend registration", {
    "Claimant name": "Synthetic amended claimant",
  });
  await decide(page);
  await completeCommand(page, "Withdraw payment decision");
  await expect(page.locator("section.case-fields").first().getByText("Revision 4")).toBeVisible();
  await expect(page.locator(".history-list details")).toHaveCount(4);
  const opening = page.locator(".history-list details").filter({ hasText: opened.operationId });
  await expect(opening).toHaveCount(1);
  await opening.locator("summary").click();
  await expect(opening).toContainText(opened.operationId);
  await expect(page.getByRole("button", { name: /^Close the case:/u })).toBeVisible();
  await expectAccessible(page);

  await page.getByRole("button", { name: "Operations", exact: true }).click();
  await page.getByLabel("Exact operation ID").fill(opened.operationId);
  await page.getByRole("button", { name: "Observe operation" }).click();
  await expect(
    page.getByText(`Accepted operation ${opened.operationId}`, { exact: false }),
  ).toBeVisible();
  await expectAccessible(page);

  await page.getByRole("button", { name: "Cases", exact: true }).click();
  await page.getByLabel("Exact case reference").fill(caseReference);
  await page.getByLabel("Exact case reference").press("Enter");
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.getByText("Synthetic amended claimant").first()).toBeVisible();
});

test("publishes payment, correction, close and reopen with current-case parity", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  await openCase(page, caseReference);
  await decide(page);
  await completeCommand(page, "Record actual payment", { "Payment date": "2026-09-05" });
  await completeCommand(page, "Correct an erroneous payment record");
  await completeCommand(page, "Close the case");
  await expect(page.getByText("CLOSED", { exact: true }).first()).toBeVisible();
  await completeCommand(page, "Reopen the case");
  await expect(page.locator("section.case-fields").first().getByText("Revision 6")).toBeVisible();
  await expect(page.locator(".history-list details")).toHaveCount(6);
  await expectAccessible(page);
});
