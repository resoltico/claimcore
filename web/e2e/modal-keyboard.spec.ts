import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { reviewDraft, startOpen } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";

test("contains keyboard focus and preserves the reviewed draft through guarded Escape and return to editing", async ({
  page,
}) => {
  await openAuthenticated(page);
  const open = page.getByRole("link", { name: "Open a case", exact: true });
  await open.focus();
  await page.keyboard.press("Enter");
  const initial = page.getByRole("dialog", { name: "Open a case", exact: true });
  await expect(initial.getByRole("heading", { name: "Open a case", exact: true })).toBeVisible();
  await initial.getByRole("button", { name: "Close", exact: true }).first().focus();
  await page.keyboard.press("Enter");
  await expect(initial).toHaveCount(0);
  await expect(open).toBeFocused();
  const reference = `KEYBOARD-${randomUUID()}`;
  const executions: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions.push(request.postData() ?? "");
    }
  });
  await startOpen(page, reference);
  const identity = await reviewDraft(page);
  const review = page.getByRole("dialog", { name: "Open a case", exact: true });
  await expectAccessible(page);
  const consent = review.locator("button[data-confirm-operation]");
  await expect(consent).toBeEnabled();
  await expect(review.getByRole("checkbox")).toHaveCount(0);
  await consent.focus();
  for (const key of ["Tab", "Shift+Tab"] as const) {
    for (let index = 0; index < 4; index += 1) {
      await page.keyboard.press(key);
      expect(await review.evaluate((element) => element.contains(document.activeElement))).toBe(
        true,
      );
    }
  }
  await page.keyboard.press("Escape");
  const guard = page.getByRole("dialog");
  await expect(guard).toContainText("Discard this draft and leave the editor?");
  await guard.getByRole("button", { name: "Keep editing", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(review).toBeVisible();
  await expect(review.locator(".review-request")).toContainText(identity.operationId);
  await review.getByRole("button", { name: "Return to editing", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(review.getByRole("button", { name: "Review changes", exact: true })).toBeFocused();
  await expect(review.locator('[data-field-name="caseReference"]')).toHaveValue(reference);
  expect(executions).toEqual([]);
});
