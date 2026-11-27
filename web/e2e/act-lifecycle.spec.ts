import { expectCaseRevision } from "./work-workflow";
import { goToView, showOperationLookup } from "./workspace-navigation";
import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";

import { expect, test, type Page } from "./fixtures";

import {
  completeAct,
  openCase,
  lookupCase,
  caseActionButton,
  startClose,
  reviewDraft,
  recordReviewed,
} from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

const expectRevision = async (page: Page, revision: string) => {
  await expectCaseRevision(page, Number(revision));
};
const reference = (): string => `BROWSER-${randomUUID().slice(0, 8)}`;

const decide = async (page: Page): Promise<void> => {
  await completeAct(page, "case.decide", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "300.25",
    "Payable currency": "EUR",
  });
};

test("publishes open, amendment, decision, withdrawal, full history and operation lookup", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  const opened = await openCase(page, caseReference);
  await completeAct(page, "case.amendRegistration", {
    "Policy number": "Synthetic amended claimant",
  });
  await decide(page);
  await completeAct(page, "case.withdrawDecision");
  await expectRevision(page, "4");
  await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(4);
  const opening = page.locator(
    `.case-timeline-panel .region-timeline > li[data-effect$=".${opened.operationId}"]`,
  );
  await expect(opening).toHaveCount(1);
  await expect(opening).toContainText("Case facts recorded");
  await opening.getByRole("button", { name: "View details", exact: true }).click();
  const details = page.getByRole("dialog", { name: "View details", exact: true });
  await expect(details).toContainText(opened.operationId);
  await details.getByRole("button", { name: "Close", exact: true }).first().click();
  await expect(await caseActionButton(page, "case.close", "en")).toBeVisible();
  await page.keyboard.press("Escape");
  await expectAccessible(page);

  await goToView(page, "recovery");
  await (await showOperationLookup(page)).fill(opened.operationId);
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await page.getByRole("button", { name: "Technical details" }).click();
  const technical = page.getByRole("dialog", { name: "Technical details" });
  await expect(technical.locator('[data-entry="operationId"]')).toContainText(opened.operationId);
  await expectAccessible(page);
  await technical.getByRole("button", { name: "Close", exact: true }).first().click();

  await goToView(page, "cases");
  await lookupCase(page, opened.caseReference);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.getByText("Synthetic amended claimant").first()).toBeVisible();
});

test("publishes payment, correction, close and reopen with current-case parity", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  const opened = await openCase(page, caseReference);
  await decide(page);
  const payment = await completeAct(page, "case.recordPayment", {
    "Payment date": "2026-09-05",
    "Paid amount": "300.25",
    "Payment currency": "EUR",
  });
  await page
    .locator('.case-detail [data-region="PAYMENTS"] tbody tr')
    .filter({ hasText: "300.25 EUR" })
    .locator("button[aria-haspopup]")
    .click();
  await page.getByRole("menuitem", { name: "Remove payment entry", exact: true }).click();
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await startClose(page);
  await reviewDraft(page);
  await recordReviewed(page);
  await expect(page.getByText("Closed", { exact: true }).first()).toBeVisible();
  await completeAct(page, "case.reopen", { Reason: "Review additional evidence" });
  await expectRevision(page, "6");
  const entries = page.locator(".case-timeline-panel .region-timeline > li");
  await expect(entries).toHaveCount(8);
  let caseReferences = 0;
  let paymentReferences = 0;
  for (const entry of await entries.all()) {
    await entry.getByRole("button", { name: "View details", exact: true }).click();
    const details = page.getByRole("dialog", { name: "View details", exact: true });
    caseReferences += await details.locator(`[data-entry="${opened.operationId}.id"]`).count();
    paymentReferences += await details.locator(`[data-entry="${payment.operationId}.id"]`).count();
    await details.getByRole("button", { name: "Close", exact: true }).first().click();
  }
  expect(caseReferences).toBe(6);
  expect(paymentReferences).toBe(2);
  await expectAccessible(page);
});
