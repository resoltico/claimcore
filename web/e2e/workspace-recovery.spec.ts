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

test("authority technical refusal preserves authored bytes and requires fresh review after edit", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByRole("button", { name: "Authority", exact: true }).click();
  await page.locator('[name="target.subject"]').fill(`repair-${randomUUID()}`);
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await expect(page.getByText("Empty input", { exact: true })).toBeVisible();
  await page.getByRole("checkbox").check();
  const event = page.waitForResponse((reply) => reply.url().endsWith("/api/authority.execute"));
  await page.getByRole("button", { name: "Submit operation", exact: true }).click();
  expect((await event).status()).toBe(400);
  await expect(page.getByRole("alert")).toContainText("issuer");
  await page.getByRole("button", { name: "Edit input", exact: true }).click();
  await expect(page.locator('[name="target.issuer"]')).toBeFocused();
  await expect(page.locator('[name="target.subject"]')).toHaveValue(/^repair-/u);
  await page.locator('[name="target.issuer"]').fill("https://issuer.example/");
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await expect(page.getByRole("checkbox")).not.toBeChecked();
  await expect(page.getByRole("button", { name: "Submit operation", exact: true })).toBeDisabled();
  await expectAccessible(page);
});
