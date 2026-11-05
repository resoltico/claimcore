import { openRecovery, goToView, showOperationLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

import { completeAct, openCase, lookupCase } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

const expectRevision = async (page: Page, revision: string) => {
  const details = page.locator("[data-record-details]");
  await details.locator(":scope > summary").click();
  await expect(
    details
      .locator("dl > div")
      .filter({ has: page.getByText("Revision", { exact: true }) })
      .locator("dd"),
  ).toHaveText(revision);
  await details.locator(":scope > summary").click();
};
const reference = (): string => `BROWSER-${randomUUID()}`;

const decide = async (page: Page): Promise<void> => {
  await completeAct(page, "Record payment decision", {
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
  await completeAct(page, "Update claim details", {
    "Claimant name": "Synthetic amended claimant",
  });
  await decide(page);
  await completeAct(page, "Withdraw payment decision");
  await expectRevision(page, "4");
  await expect(page.locator(".case-timeline > .timeline-entry")).toHaveCount(4);
  const opening = page
    .locator(".case-timeline > .timeline-entry")
    .filter({ hasText: opened.operationId });
  await expect(opening).toHaveCount(1);
  await opening.locator(":scope > details > summary").click();
  await expect(opening.locator(".timeline-change").first()).toBeVisible();
  await expect(opening).toContainText("Synthetic claimant");
  await expect(opening).toContainText("1,200.50");
  await expect(opening).toContainText(opened.operationId);
  const close = page.getByRole("button", { name: /^Close the case:/u });
  if (!(await close.isVisible())) {
    await page
      .locator(".case-actions > summary, .case-more-actions > summary")
      .filter({ visible: true })
      .click();
  }
  await expect(close).toBeVisible();
  await expectAccessible(page);

  await openRecovery(page);
  await (await showOperationLookup(page)).fill(opened.operationId);
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(
    page.getByText(`Accepted operation ${opened.operationId}`, { exact: false }),
  ).toBeVisible();
  await expectAccessible(page);

  await goToView(page, "cases");
  await lookupCase(page, caseReference);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.getByText("Synthetic amended claimant").first()).toBeVisible();
});

test("publishes payment, correction, close and reopen with current-case parity", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  await openCase(page, caseReference);
  await decide(page);
  await completeAct(page, "Record actual payment", { "Payment date": "2026-09-05" });
  await completeAct(page, "Correct an erroneous payment record");
  await completeAct(page, "Close the case");
  await expect(page.getByText("Closed", { exact: true }).first()).toBeVisible();
  await completeAct(page, "Reopen the case");
  await expectRevision(page, "6");
  await expect(page.locator(".case-timeline > .timeline-entry")).toHaveCount(6);
  await expectAccessible(page);
});
