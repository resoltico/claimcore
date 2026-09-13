import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { expectAccessible, openAuthenticated } from "./session-helpers";

test("shows exact case and operation misses without implying a failed mutation", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByLabel("Exact case reference").fill(`ABSENT-${randomUUID()}`);
  await page.getByLabel("Exact case reference").press("Enter");
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  const missingCase = page.getByText("Case was not found.", { exact: true });
  await expect(missingCase).toHaveAttribute("role", "status");
  await expect(page.getByText("Loading accepted history…")).toHaveCount(0);
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expectAccessible(page);
  await page.getByRole("button", { name: "Operations", exact: true }).click();
  await page.getByLabel("Exact operation ID").fill(randomUUID());
  await page.getByRole("button", { name: "Observe operation" }).click();
  const missingOperation = page.getByText("Operation was not observed.", { exact: true });
  await expect(missingOperation).toHaveAttribute("role", "status");
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expectAccessible(page);
});
