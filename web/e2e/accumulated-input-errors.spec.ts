import { type ResponseFor } from "@contracts/client";
import { fillBusinessField } from "./typed-entry";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { startOpen } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { trackForegroundRequests } from "./localization-support";
test("one submit reports every invalid input in summary order and links to its field", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `ACCUMULATE-${randomUUID()}`);
  const incident = page.locator('[data-field-name="incidentDate"]').getByRole("spinbutton").first();
  const amount = page.getByLabel("Claimed amount", {
    exact: true,
  });
  const currency = page.getByRole("combobox", {
    name: "Claimed currency",
    exact: true,
  });
  await fillBusinessField(page, "incidentDate", "");
  await amount.fill("00.10");
  await currency.fill("euros");
  const requests = trackForegroundRequests(page);
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await page
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const reply = await pending;
  expect(reply.status()).toBe(200);
  const operationPreviewReply = (await reply.json()) as ResponseFor<"operation.preview", "HUMAN">;
  if (
    operationPreviewReply.representation !== "HUMAN" ||
    operationPreviewReply.outcome.kind !== "REJECTED" ||
    operationPreviewReply.outcome.rejection.kind !== "INPUT"
  ) {
    throw new Error("Expected authoritative accumulated input refusal.");
  }
  expect(
    operationPreviewReply.outcome.rejection.issues.map((issue) => ({
      input: issue.input,
      row: issue.row,
      messageId: issue.message.messageId,
    })),
  ).toEqual([
    {
      input: "field:incidentDate",
      row: null,
      messageId: "diagnostic.CALENDAR_DATE_REQUIRED",
    },
    {
      input: "field:claimedAmount",
      row: null,
      messageId: "diagnostic.DECIMAL_FORMAT",
    },
    {
      input: "field:claimedCurrency",
      row: null,
      messageId: "diagnostic.CURRENCY_FORMAT",
    },
  ]);
  expect(requests).toEqual(["POST /api/operation.preview"]);
  await expect(page.getByRole("alert")).toContainText("Fields to check: 3.");
  for (const control of [incident, amount, currency]) {
    await expect(control).toHaveAttribute("aria-invalid", "true");
    await expect(control).toHaveAttribute("aria-describedby", /\S/u);
  }
  await expect(incident).toHaveAccessibleDescription(
    "When the incident happened. Enter a real date.",
  );
  await expect(amount).toHaveAccessibleDescription(/decimal/u);
  await expect(currency).toHaveAccessibleDescription(/currency/u);
  const summary = page.locator("[data-error-summary]");
  await expect(summary).toBeFocused();
  await expect(summary.getByRole("link")).toHaveCount(3);
  await expect(summary.getByRole("link").first()).toContainText("Incident date");
  await summary.getByRole("link").first().click();
  await expect(incident).toBeFocused();
  await expect(
    page.getByRole("dialog", {
      name: "Open a case",
      exact: true,
    }),
  ).toBeVisible();
  await expect(page.locator("[data-confirm-operation]")).toHaveCount(0);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expectAccessible(page);
});
