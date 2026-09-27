import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { expectAccessible, openAuthenticated } from "./session-helpers";

test("hides absent case and operation identities behind neutral access refusals", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByLabel("Exact case reference").fill(`ABSENT-${randomUUID()}`);
  await page.getByLabel("Exact case reference").press("Enter");
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Case was not found.", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Loading accepted history…")).toHaveCount(0);
  await expectAccessible(page);
  await page.getByRole("button", { name: "Operations", exact: true }).click();
  await page.getByLabel("Exact operation ID").fill(randomUUID());
  await page.getByRole("button", { name: "Observe operation" }).click();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Operation was not observed.", { exact: true })).toHaveCount(0);
  await expectAccessible(page);
});
