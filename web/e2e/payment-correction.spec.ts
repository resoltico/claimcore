import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, test } from "./fixtures";
import type { RequestByEndpoint, ResponseFor } from "@contracts/client";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, completeAct } from "./case-workflow";
import { acceptedSubject, workRequest } from "./work-workflow";
import { fillNamedField } from "./typed-entry";
import { expectCaseReadsSettled } from "./case-readiness";
import { noHorizontalOverflow } from "./layout-support";

test("corrects selected payment atomically with both identities in receipt timeline and CSV @engine", async ({
  page,
}) => {
  test.setTimeout(60_000);
  await openAuthenticated(page);
  const { caseReference: reference } = await openCase(page, `CORRECT-${randomUUID().slice(0, 8)}`);
  await completeAct(page, "case.decide", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "100",
    "Payable currency": "EUR",
  });
  await completeAct(page, "case.recordPayment", {
    "Payment date": "2026-09-04",
    "Paid amount": "100",
    "Payment currency": "EUR",
  });
  const target = { kind: "REFERENCE" as const, caseReference: reference };
  const before = await workRequest(page, "case.get", { target });
  if (before.outcome.kind !== "SUCCEEDED" || before.outcome.data.state.kind === "ERASED") {
    throw new Error("Current paid case required");
  }
  const [old] = before.outcome.data.latestPayments;
  if (old === undefined) {
    throw new Error("Original accepted payment required");
  }
  const payments = page.locator('.case-detail .case-payments[data-region="PAYMENTS"]');
  let writes = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      writes += 1;
    }
  });
  await payments
    .locator("tbody tr")
    .filter({ hasText: "100.00 EUR" })
    .locator("button[aria-haspopup]")
    .click();
  await page.getByRole("menuitem", { name: "Correct payment entry", exact: true }).click();
  const dialog = page.getByRole("dialog");
  const editAmount = dialog.getByRole("checkbox", { name: "Change Paid amount", exact: true });
  const reload = dialog.getByRole("button", { name: "Reload current case", exact: true });
  await expect(editAmount.or(reload)).toBeVisible();
  if (await reload.isVisible()) {
    await reload.click();
  }
  await expect(dialog.getByRole("textbox", { name: "Payment ID", exact: true })).toHaveCount(0);
  await editAmount.check();
  await expect(
    dialog.getByRole("checkbox", { name: "Change Payment currency", exact: true }),
  ).not.toBeChecked();
  await fillNamedField(page, "Paid amount", "75");
  const previewed = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await dialog.getByRole("button", { name: "Review changes", exact: true }).click();
  const preview = await previewed;
  const frozen = preview.request().postDataJSON() as RequestByEndpoint["operation.execute"];
  expect(frozen).toMatchObject({
    task: "case.correctPayment",
    binding: {
      kind: "CASE",
      expectedRevision: before.outcome.data.revision,
      paymentId: old.paymentId,
    },
    inputs: {
      "field:paidAmount": "75",
    },
  });
  expect(frozen.inputs).toEqual({ "field:paidAmount": "75" });
  const review = (await preview.json()) as ResponseFor<"operation.preview", "HUMAN">;
  expect(JSON.stringify(review)).toContain(old.paymentId);
  expect(JSON.stringify(review)).toContain(frozen.operationId);
  expect(writes).toBe(0);
  await expectAccessible(page);
  const executed = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
  );
  await dialog.locator("button[data-confirm-operation]").click();
  const response = await executed;
  expect(response.request().postData()).toBe(preview.request().postData());
  const human = (await response.json()) as ResponseFor<"operation.execute", "HUMAN">;
  if (human.outcome.kind !== "ACCEPTED") {
    throw new Error("One accepted correction required");
  }
  expect(JSON.stringify(human.outcome.document)).toContain(old.paymentId);
  expect(JSON.stringify(human.outcome.document)).toContain(frozen.operationId);
  expect(JSON.stringify(human.outcome.document)).toContain("task.case.removePayment.done");
  expect(JSON.stringify(human.outcome.document)).toContain("task.case.recordPayment.done");
  await expectCaseReadsSettled(page);
  expect(writes).toBe(1);
  const subject = await acceptedSubject(page, frozen.operationId);
  if (subject.kind !== "CASE" || subject.snapshot.kind !== "DISCLOSED") {
    throw new Error("Authorized plural DATA receipt required");
  }
  expect(subject.revision).toBe(before.outcome.data.revision + 1);
  expect(subject.snapshot.payments.map((value) => [value.kind, value.entry.paymentId])).toEqual([
    ["REMOVED", old.paymentId],
    ["RECORDED", frozen.operationId],
  ]);
  const after = await workRequest(page, "case.get", { target });
  if (after.outcome.kind !== "SUCCEEDED") {
    throw new Error("Current corrected case required");
  }
  expect(after.outcome.data.latestPayments.map((value) => value.paymentId)).toEqual([
    frozen.operationId,
  ]);
  await expect(payments.locator('[data-entry="netPaid.EUR"]')).toContainText("75.00 EUR");
  await expect(payments.locator('[data-entry="payable"]')).toContainText("100.00 EUR");
  const history = await workRequest(page, "case.timeline", { target, filter: "FACTS", limit: 50 });
  if (history.outcome.kind !== "SUCCEEDED") {
    throw new Error("Current timeline required");
  }
  const effects = history.outcome.data.items.filter(
    (value) => value.operationId === frozen.operationId,
  );
  expect(
    effects.map((value) => value.position.effectIndex).sort((left, right) => left - right),
  ).toEqual([0, 1, 2]);
  const downloaded = page.waitForEvent("download");
  await page.locator(".case-timeline-panel").getByRole("button", { name: "Download CSV" }).click();
  const download = await downloaded;
  try {
    const path = await download.path();
    if (path === null) {
      throw new Error("Complete timeline CSV required");
    }
    const text = (await readFile(path)).toString("utf8");
    expect(text).toContain(`payment.${old.paymentId}.amount`);
    expect(text).toContain(`payment.${frozen.operationId}.amount`);
    expect(text).toContain("case.correctPayment");
  } finally {
    await download.delete();
  }
  await page.setViewportSize({ width: 320, height: 900 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
});
