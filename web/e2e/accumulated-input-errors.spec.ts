import { fillBusinessField } from "./typed-entry";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { startOpen } from "./case-workflow";
import { isResponse } from "../src/protocol/client";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { trackRequests } from "./localization-support";

test("one submit reports every invalid input in summary order and links to its field", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `ACCUMULATE-${randomUUID()}`);
  const incident = page.locator('[data-field-name="incidentDate"]').getByRole("spinbutton").first();
  const amount = page.getByLabel("Amount claimed", { exact: true });
  const currency = page.getByRole("combobox", { name: "Currency of claimed amount", exact: true });
  await fillBusinessField(page, "incidentDate", "");
  await amount.fill("00.10");
  await currency.fill("euros");
  const requests = trackRequests(page);
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  const reply = await pending;
  expect(reply.status()).toBe(200);
  const value: unknown = await reply.json();
  if (
    !isResponse("operation.preview", value) ||
    value.outcome.kind !== "REJECTED" ||
    value.outcome.data.code !== "INVALID_INPUT"
  ) {
    throw new Error("Expected authoritative accumulated input refusal.");
  }
  expect(value.outcome.data.issues.map(({ field, code }) => ({ field, code }))).toEqual([
    { field: "incidentDate", code: "CALENDAR_DATE_REQUIRED" },
    { field: "claimedAmount", code: "DECIMAL_FORMAT" },
    { field: "claimedCurrency", code: "CURRENCY_FORMAT" },
  ]);
  expect(requests).toEqual(["POST /api/operation.preview"]);
  await expect(page.getByRole("alert")).toContainText("Input problems: 3.");
  for (const control of [incident, amount, currency]) {
    await expect(control).toHaveAttribute("aria-invalid", "true");
    await expect(control).toHaveAttribute("aria-describedby", /\S/u);
  }
  await expect(incident).toHaveAccessibleDescription(/calendar date/u);
  await expect(amount).toHaveAccessibleDescription(/decimal/u);
  await expect(currency).toHaveAccessibleDescription(/currency/u);
  const summary = page.locator("[data-error-summary]");
  await expect(summary).toBeFocused();
  await expect(summary.getByRole("link")).toHaveCount(3);
  await expect(summary.getByRole("link").first()).toContainText("Incident date");
  await summary.getByRole("link").first().click();
  await expect(incident).toBeFocused();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expectAccessible(page);
});
