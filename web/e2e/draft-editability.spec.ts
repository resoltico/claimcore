import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, reviewDraft, startAct, startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

test("protects exact routed amendment edits on Back while untouched drafts leave directly", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `DRAFT-${randomUUID()}`);
  await startAct(page, "Amend registration");
  await expect(page.getByRole("combobox", { name: "Case action", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Back to case", exact: true }).click();
  await startAct(page, "Close the case");
  await expect(page.getByRole("heading", { name: "Close the case" })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("button", { name: "Back to case", exact: true }).click();
  await startAct(page, "Amend registration");
  const selectedFacts = page.locator(".operation-editor form input[data-selects-fields]");
  await selectedFacts.first().waitFor({ state: "visible" });
  for (const selected of await selectedFacts.all()) {
    await selected.check();
  }
  const fields = page.locator(".operation-editor form [data-field-name]");
  await expect(fields).toHaveCount(7);
  for (const name of Object.keys(registration())) {
    await fillBusinessField(page, name, "");
  }
  await page.getByRole("button", { name: "Back to case", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await page.getByRole("button", { name: "Keep editing" }).click();
  await page.getByRole("button", { name: "Back to case" }).click();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Discard and leave", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.locator('.field-row[data-field-name="claimedAmount"] bdi').first()).toHaveText(
    "1,200.50",
  );
  await startAct(page, "Amend registration");
  await page.getByRole("button", { name: "Back to case" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
});

test("review blocks authoring and accepted OPEN returns to the recorded case with a revision notice", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `RECEIPT-${randomUUID()}`);
  await reviewDraft(page);
  for (const input of await page
    .locator(
      '.operation-editor form input:not([type="hidden"]), .operation-editor form [role="spinbutton"]',
    )
    .all()) {
    await expect(input).toBeDisabled();
  }
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes" }).click();
  const detail = page.getByRole("heading", { name: "Case detail", exact: true });
  await expect(detail).toBeVisible();
  await expect(detail).toBeFocused();
  await expect(page.locator(".success-toast")).toContainText("Open a case recorded at revision 1.");
  await expect(page.locator(".operation-editor form")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Back to cases", exact: true })).toBeVisible();
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Amend registration:/u })).toBeVisible();
});
