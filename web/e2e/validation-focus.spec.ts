import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { startOpen } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

test("focuses and describes the named field after a published core rejection", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `INVALID-${randomUUID()}`);
  const amount = page.getByLabel("Amount claimed", { exact: true });
  await amount.fill("00.10");
  await page.getByRole("button", { name: "Prepare exact request" }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  await expect(amount).toBeFocused();
  await expect(amount).toHaveAttribute("aria-invalid", "true");
  await expect(amount).toHaveAttribute("aria-describedby", /\S/u);
  await expectAccessible(page);
});
