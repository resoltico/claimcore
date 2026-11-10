import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, completeAct } from "./case-workflow";
import { workRequest } from "./work-workflow";
import { noHorizontalOverflow } from "./layout-support";

test("records instalments and removes the selected entry through one reviewed confirmation @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `PAY-${randomUUID()}`;
  await openCase(page, reference);
  await completeAct(page, "DECIDE", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "100",
    "Payable currency": "EUR",
  });
  await completeAct(page, "RECORD_PAYMENT", { "Payment date": "2026-09-04", "Paid amount": "40" });
  const payments = page.locator('[data-region="PAYMENTS"]').first();
  await expect(payments.locator('[data-entry="paid"]')).toContainText("40.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("60.00 EUR");
  await expect(
    page.locator('.code-value[data-vocabulary="progress"][data-code="PARTLY_PAID"]'),
  ).toBeVisible();
  await expect(
    page.locator('.case-stage-list li[data-complete="false"] meter[aria-label="Paid"]'),
  ).toHaveAttribute("value", "40");
  await completeAct(page, "RECORD_PAYMENT", { "Payment date": "2026-09-05", "Paid amount": "20" });
  await expect(payments.locator('[data-entry="paid"]')).toContainText("60.00 EUR");
  const read = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (read.outcome.kind !== "SUCCEEDED" || read.outcome.data.state.kind === "ERASED") {
    throw Error("Live payment read required");
  }
  const first = read.outcome.data.latestPayments.find((entry) => entry.paidAmount === "40");
  if (first === undefined) {
    throw Error("First accepting payment identity required");
  }
  let writes = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      writes += 1;
    }
  });
  const preview = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await payments
    .locator("tbody tr")
    .filter({ hasText: "40.00 EUR" })
    .getByRole("button", { name: "Remove payment entry", exact: true })
    .click();
  const reviewed = await preview;
  expect(reviewed.request().postDataJSON()).toMatchObject({
    kind: "CASE",
    expectedRevision: 4,
    act: { kind: "REMOVE_PAYMENT", paymentId: first.paymentId },
  });
  const dialog = page.getByRole("dialog");
  const confirm = dialog.locator("button[data-confirm-operation]");
  await expect(confirm).toBeEnabled();
  expect(writes).toBe(0);
  await expect(dialog.getByRole("textbox", { name: "Payment ID", exact: true })).toHaveCount(0);
  await expectAccessible(page);
  await confirm.click();
  await expect(dialog).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await expect(payments.locator('[data-entry="paid"]')).toContainText("20.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("80.00 EUR");
  await expect(payments.locator("tbody tr")).toHaveCount(1);
  expect(writes).toBe(1);
  await page.setViewportSize({ width: 320, height: 900 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
});
