import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { prepare, startOpen } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

test("contains keyboard focus and returns it after safely dismissing a prepared review", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByRole("button", { name: "Open new case" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Open a case" })).toBeVisible();
  await page.getByRole("button", { name: "Back without preparing" }).click();
  await startOpen(page, `KEYBOARD-${randomUUID()}`);
  await prepare(page);
  const review = page.getByRole("dialog", { name: "Review prepared operation" });
  await expectAccessible(page);
  await review.getByRole("checkbox").focus();
  for (let index = 0; index < 4; index += 1) {
    await page.keyboard.press("Tab");
    expect(await review.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  }
  await page.keyboard.press("Escape");
  await expect(review).not.toBeVisible();
  await expect(page.getByRole("button", { name: "Prepare exact request" })).toBeFocused();
});
