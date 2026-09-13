import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { prepare, startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

test("rotates a retained OPEN identity when only its target reference changes", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `FIRST-${randomUUID()}`);
  const first = await prepare(page);
  await page.getByRole("button", { name: "Keep for Recovery" }).click();
  await expect(page.getByRole("dialog", { name: "Review prepared operation" })).not.toBeVisible();
  await page.getByLabel("Handler's case reference", { exact: true }).fill(`SECOND-${randomUUID()}`);
  const second = await prepare(page);
  expect(second.operationId).not.toBe(first.operationId);
  expect(second.requestSha256).not.toBe(first.requestSha256);
});
