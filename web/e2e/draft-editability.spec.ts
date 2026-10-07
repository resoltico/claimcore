import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, prepare, startCommand, startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

test("protects exact amendment edits on action change and Back while untouched drafts leave directly", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `DRAFT-${randomUUID()}`);
  await startCommand(page, "Amend registration");
  await page.getByLabel("Case action").selectOption("CLOSE");
  await expect(page.getByRole("heading", { name: "Close the case" })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByLabel("Case action").selectOption("AMEND_REGISTRATION");
  const fields = page.locator(".operation-editor form input");
  await expect(fields).toHaveCount(7);
  for (const input of await fields.all()) {
    await input.fill("");
  }
  await page.getByLabel("Case action").selectOption("CLOSE");
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await page.getByRole("button", { name: "Keep editing" }).click();
  await page.getByRole("button", { name: "Back to case" }).click();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await page.getByRole("button", { name: "Discard and leave" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.locator('.field-row[data-field-name="claimedAmount"] bdi').first()).toHaveText(
    "1,200.5",
  );
  await startCommand(page, "Amend registration");
  await page.getByRole("button", { name: "Back to case" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
});

test("review blocks authoring and accepted OPEN shows only receipt with a usable Return path", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `RECEIPT-${randomUUID()}`);
  await prepare(page);
  for (const input of await page.locator(".operation-editor form input").all()) {
    await expect(input).toBeDisabled();
  }
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes" }).click();
  const receipt = page.getByRole("heading", { name: "Recorded change", exact: true });
  await expect(receipt).toBeVisible();
  await expect(receipt).toBeFocused();
  await expect(page.locator(".operation-editor form")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Back to cases", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Return to case" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Amend registration:/u })).toBeVisible();
});
