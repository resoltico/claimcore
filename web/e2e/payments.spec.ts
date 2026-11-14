import { type ResponseFor, type RequestByEndpoint } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";
import { expect, test, type Page, type Response } from "@playwright/test";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { selectLanguage, selectFormat, selectMode, ui } from "./localization-support";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, completeAct, recordReviewed } from "./case-workflow";
import { workRequest } from "./work-workflow";
import { noHorizontalOverflow } from "./layout-support";
test("records instalments and removes the selected entry through one reviewed confirmation @engine", async ({
  page,
}) => {
  // Five reviewed writes, readbacks and accessibility scans share this scenario budget.
  // Individual request and assertion deadlines remain unchanged.
  test.setTimeout(60_000);
  await openAuthenticated(page);
  const reference = `PAY-${randomUUID()}`;
  await openCase(page, reference);
  await completeAct(page, "case.decide", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "100",
    "Payable currency": "EUR",
  });
  await completeAct(page, "case.recordPayment", {
    "Payment date": "2026-09-04",
    "Paid amount": "40",
  });
  const payments = page.locator('.case-detail [data-region="PAYMENTS"]').first();
  await expect(payments.locator('[data-entry="paid"]')).toContainText("40.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("60.00 EUR");
  await expect(
    page.locator('.case-detail .code-value[data-vocabulary="progress"][data-code="PARTLY_PAID"]'),
  ).toBeVisible();
  await expect(
    page.locator(
      '.case-detail .case-stage-list li[data-complete="false"] meter[aria-label="Paid"]',
    ),
  ).toHaveAttribute("value", "40");
  await completeAct(page, "case.recordPayment", {
    "Payment date": "2026-09-05",
    "Paid amount": "20",
  });
  await expect(payments.locator('[data-entry="paid"]')).toContainText("60.00 EUR");
  const read = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
    .filter({
      hasText: "40.00 EUR",
    })
    .getByRole("button", {
      name: "Remove payment entry",
      exact: true,
    })
    .click();
  const reviewed = await preview;
  expect(reviewed.request().postDataJSON()).toMatchObject({
    task: "case.removePayment",
    binding: { kind: "CASE", expectedRevision: 4, paymentId: first.paymentId },
    inputs: {},
  });
  const dialog = page.getByRole("dialog");
  const confirm = dialog.locator("button[data-confirm-operation]");
  await expect(confirm).toBeEnabled();
  expect(writes).toBe(0);
  await expect(
    dialog.getByRole("textbox", {
      name: "Payment ID",
      exact: true,
    }),
  ).toHaveCount(0);
  await expectAccessible(page);
  await confirm.click();
  await expect(dialog).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await expect(payments.locator('[data-entry="paid"]')).toContainText("20.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("80.00 EUR");
  await expect(payments.locator("tbody tr")).toHaveCount(1);
  expect(writes).toBe(1);
  await page.setViewportSize({
    width: 320,
    height: 900,
  });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
});
const paymentDocument = async (response: Response) => {
  const casePaymentsReply = (await response.json()) as ResponseFor<"case.payments", "HUMAN">;
  if (
    response.status() !== 200 ||
    casePaymentsReply.representation !== "HUMAN" ||
    casePaymentsReply.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("Expected a current HUMAN payment page.");
  }
  return casePaymentsReply.outcome.document;
};
const recordPayments = async (page: Page, caseId: string) => {
  const commands: Extract<RequestByEndpoint["operation.execute"], { binding: { kind: "CASE" } }>[] =
    [];
  let revision = 2;
  for (let index = 0; index < 55; index += 1) {
    const command: Extract<RequestByEndpoint["operation.execute"], { binding: { kind: "CASE" } }> =
      {
        operationId: mintOperationId(),
        task: "case.recordPayment",
        binding: {
          kind: "CASE",
          target: {
            kind: "ID",
            caseId,
          },
          expectedRevision: revision,
        },
        inputs: {
          "field:paymentDate": "2026-09-04",
          "field:paidAmount": "1",
          "field:paidCurrency": "EUR",
        },
      };
    const result = await workRequest(page, "operation.execute", command);
    if (result.outcome.kind !== "ACCEPTED" || result.outcome.data.subject.kind !== "CASE") {
      throw new Error("A fresh payment must append one genuine case operation.");
    }
    expect(result.operationId).toBe(command.operationId);
    expect(result.outcome.data.subject.revision).toBe(revision + 1);
    ({ revision } = result.outcome.data.subject);
    commands.push(command);
  }
  return commands;
};
const expandPayments = async (page: Page, sizes: ReadonlyArray<number>) => {
  const section = page.locator('.case-detail [data-region="PAYMENTS"]').first();
  const ids: string[] = [];
  for (const [index, size] of sizes.entries()) {
    const pending = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.payments",
    );
    await section
      .getByRole("button", {
        name: index === 0 ? "Show all payments" : "More payments",
        exact: true,
      })
      .click();
    const response = await pending;
    const document = await paymentDocument(response);
    expect(response.request().postDataJSON()).not.toHaveProperty("limit");
    expect(document.context.payments).toHaveLength(size);
    ids.push(...document.context.payments.map((handle) => handle.paymentId));
    await expect(section.locator(".case-payment-pages tbody tr")).toHaveCount(ids.length);
  }
  await expect(
    section.getByRole("button", {
      name: "More payments",
      exact: true,
    }),
  ).toHaveCount(0);
  return ids;
};
test("expands more than fifty genuine payments, removes an older entry and reflows as Arabic cards", async ({
  page,
}, info) => {
  test.setTimeout(120_000);
  await openAuthenticated(page);
  await page.setViewportSize({
    width: 1440,
    height: 1000,
  });
  const reference = "CLM-PAYMENTS-55";
  await openCase(page, reference);
  await completeAct(page, "case.decide", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "100",
    "Payable currency": "EUR",
  });
  const initial = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (initial.outcome.kind !== "SUCCEEDED") {
    throw new Error("Case identity required for payment setup.");
  }
  const { caseId } = initial.outcome.data;
  expect(initial.outcome.data.revision).toBe(2);
  const commands = await recordPayments(page, caseId);
  await page.reload();
  await expectCaseReadsSettled(page);
  const payments = page.locator('.case-detail [data-region="PAYMENTS"]').first();
  await expect(payments.locator('[data-entry="paymentCount"] dd')).toHaveText("55");
  await expect(payments.locator('[data-entry="paid"]')).toContainText("55.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("45.00 EUR");
  await expect(payments.locator("tbody tr")).toHaveCount(3);
  await page.screenshot({
    path: info.outputPath("workspace-payments-en-light.png"),
  });
  const ids = await expandPayments(page, [20, 20, 15]);
  expect(ids).toEqual(commands.map((command) => command.operationId).toReversed());
  expect(new Set(ids).size).toBe(55);
  await expectAccessible(page);
  await payments.scrollIntoViewIfNeeded();
  await payments.screenshot({
    path: info.outputPath("paged-payments-en-light.png"),
  });
  const [original] = commands;
  if (original === undefined) {
    throw new Error("The oldest recorded payment is required.");
  }
  const older = payments.locator("tbody tr").filter({
    has: page.getByText(original.operationId, {
      exact: true,
    }),
  });
  const preview = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await older
    .getByRole("button", {
      name: "Remove payment entry",
      exact: true,
    })
    .click();
  const reviewed = await preview;
  expect(reviewed.request().postDataJSON()).toMatchObject({
    task: "case.removePayment",
    binding: { kind: "CASE", expectedRevision: 57, paymentId: original.operationId },
    inputs: {},
  });
  const execution = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
  );
  await recordReviewed(page);
  expect((await execution).request().postData()).toBe(reviewed.request().postData());
  await expect(payments.locator('[data-entry="paymentCount"] dd')).toHaveText("54");
  await expect(payments.locator('[data-entry="paid"]')).toContainText("54.00 EUR");
  await expect(payments.locator('[data-entry="remaining"]')).toContainText("46.00 EUR");
  await expect(payments.locator("tbody tr")).toHaveCount(3);
  const replay = await workRequest(page, "operation.execute", original);
  expect(replay.outcome.kind).toBe("ALREADY_ACCEPTED");
  const current = await workRequest(page, "case.get", {
    target: {
      kind: "ID",
      caseId,
    },
  });
  if (current.outcome.kind !== "SUCCEEDED" || current.outcome.data.state.kind === "ERASED") {
    throw new Error("Current financial position required after replay.");
  }
  expect(current.outcome.data.state.fields.finance.paymentCount).toBe(54);
  expect(current.outcome.data.state.fields.finance.paidAmount).toBe("54");
  const acceptedNotice = page.locator(".success-toast");
  await expect(acceptedNotice).toBeVisible();
  await acceptedNotice
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .click();
  await selectMode(page, "dark");
  await page.screenshot({
    path: info.outputPath("workspace-payments-en-dark.png"),
  });
  const remaining = await expandPayments(page, [20, 20, 14]);
  expect(remaining).toEqual(
    commands
      .slice(1)
      .map((command) => command.operationId)
      .toReversed(),
  );
  await selectLanguage(page, "ar");
  await selectFormat(page, "ar-EG");
  await page.setViewportSize({
    width: 320,
    height: 900,
  });
  const expand = payments.getByRole("button", {
    name: ui("ar", "ui.showAllPayments"),
    exact: true,
  });
  if (await expand.isVisible()) {
    const pending = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.payments",
    );
    await expand.click();
    await pending;
  }
  await expect(payments.locator(".payment-cards > li").first()).toBeVisible();
  await payments.locator(".payment-cards > li").first().scrollIntoViewIfNeeded();
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await page.screenshot({
    path: info.outputPath("payment-cards-ar-dark.png"),
  });
});
