import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { reviewDraft, startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

test("rotates a exposed OPEN identity when only its target reference changes", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `FIRST-${randomUUID()}`);
  const first = await reviewDraft(page);
  await page.getByRole("button", { name: "Return to editing" }).click();
  await expect(page.getByRole("dialog", { name: "Review changes" })).not.toBeVisible();
  await page.getByLabel("Handler's case reference", { exact: true }).fill(`SECOND-${randomUUID()}`);
  const second = await reviewDraft(page);
  expect(second.operationId).not.toBe(first.operationId);
  expect(second.operationId).not.toBe(first.operationId);
});
