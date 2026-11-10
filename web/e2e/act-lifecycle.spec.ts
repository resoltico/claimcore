import {
  showCaseRecordDetails,
  openRecovery,
  goToView,
  showOperationLookup,
} from "./workspace-navigation";
import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

import { completeAct, openCase, lookupCase, caseActionButton } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

const expectRevision = async (page: Page, revision: string) => {
  await showCaseRecordDetails(page);
  await expect(
    page.locator(
      "section.case-detail > .screen-view > [data-region=DETAILS] .entries-view [data-entry='revision'] dd",
    ),
  ).toContainText(revision);
};
const reference = (): string => `BROWSER-${randomUUID()}`;

const decide = async (page: Page): Promise<void> => {
  await completeAct(page, "DECIDE", {
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
  await completeAct(page, "AMEND_REGISTRATION", {
    "Claimant name": "Synthetic amended claimant",
  });
  await decide(page);
  await completeAct(page, "WITHDRAW_DECISION");
  await expectRevision(page, "4");
  await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(4);
  const opening = page
    .locator(".case-timeline-panel .region-timeline > li")
    .filter({ hasText: opened.operationId });
  await expect(opening).toHaveCount(1);
  await expect(opening).toContainText("Case opened");
  await expect(opening).toContainText(opened.operationId);
  await expect(await caseActionButton(page, "CLOSE", "en")).toBeVisible();
  await expectAccessible(page);

  await openRecovery(page);
  await (await showOperationLookup(page)).fill(opened.operationId);
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await page.locator('.receipt details[data-region="DETAILS"] > summary').click();
  await expect(page.locator('.receipt [data-entry="operationId"]')).toContainText(
    opened.operationId,
  );
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
  await completeAct(page, "RECORD_PAYMENT", {
    "Payment date": "2026-09-05",
    "Paid amount": "300.25",
  });
  await page
    .locator('.case-detail [data-region="PAYMENTS"]')
    .getByRole("button", { name: "Remove payment entry", exact: true })
    .click();
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await completeAct(page, "CLOSE");
  await expect(page.getByText("Closed", { exact: true }).first()).toBeVisible();
  await completeAct(page, "REOPEN");
  await expectRevision(page, "6");
  await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(6);
  await expectAccessible(page);
});
