import { isResponse } from "../src/protocol/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";

test("context interruption recovers through an explicit in-page read retry", async ({ page }) => {
  await openAuthenticated(page);
  await page.route("**/api/context", (route) => route.abort("connectionfailed"));
  await page.reload();
  await expect(page.getByRole("button", { name: "Try again", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Cases", exact: true })).toHaveCount(0);
  await page.unroute("**/api/context");
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  await expectAccessible(page);
});

test("keyboard navigation focuses editor and destination after return", async ({ page }) => {
  await openAuthenticated(page);
  await page.getByRole("button", { name: "Open a case", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Open a case", exact: true })).toBeFocused();
  await page.getByRole("button", { name: "Back to cases", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeFocused();
});

test("authority input refusal preserves authored bytes and requires fresh review after edit", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByRole("button", { name: "Authority", exact: true }).click();
  await page.locator('[name="target.subject"]').fill(`repair-${randomUUID()}`);
  const event = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  const response = await event;
  expect(response.status()).toBe(200);
  const reply: unknown = await response.json();
  if (!isResponse("operation.preview", reply) || reply.outcome.kind !== "REJECTED") {
    throw new Error("Expected an Application principal refusal.");
  }
  expect(reply.outcome.data).toEqual({
    code: "INVALID_INPUT",
    action: "CORRECT_INPUT",
    issues: [{ field: "principal.issuer", code: "TEXT_REQUIRED" }],
  });
  await expect(page.getByRole("alert")).toContainText("A non-blank value is required.");
  await expect(page.locator('[name="target.issuer"]')).toBeFocused();
  await expect(page.locator('[name="target.subject"]')).toHaveValue(/^repair-/u);
  await page.locator('[name="target.issuer"]').fill("https://issuer.example/");
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await expect(page.getByRole("checkbox")).not.toBeChecked();
  await expect(page.getByRole("button", { name: "Record changes", exact: true })).toBeDisabled();
  await expectAccessible(page);
});
