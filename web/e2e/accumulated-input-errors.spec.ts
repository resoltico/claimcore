import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { startOpen } from "./case-workflow";
import { isResponse } from "../src/protocol/client";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { trackRequests } from "./localization-support";

test("one submit reports every malformed input inline and focuses the first in catalog order", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `ACCUMULATE-${randomUUID()}`);
  const incident = page.getByLabel("Incident date", { exact: true });
  const amount = page.getByLabel("Amount claimed", { exact: true });
  const currency = page.getByLabel("Currency of claimed amount", { exact: true });
  await incident.fill("not-a-date");
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
  await expect(incident).toBeFocused();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expectAccessible(page);
});
