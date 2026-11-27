import { randomUUID } from "node:crypto";

import { expect, test } from "./fixtures";

import { recordReviewed, reviewDraft, startOpen } from "./case-workflow";
import { clippedControlEdges, noHorizontalOverflow } from "./layout-support";
import { openAuthenticated } from "./session-helpers";

test("rotates a exposed OPEN identity when an authored incident description changes", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `FIRST-${randomUUID().slice(0, 8)}`);
  const first = await reviewDraft(page);
  await page.getByRole("button", { name: "Return to editing" }).click();
  await expect(page.getByRole("dialog", { name: "Open a case" })).toBeVisible();
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toHaveCount(0);
  await expect(page.getByLabel("Incident description", { exact: true })).toBeVisible();
  await page
    .getByLabel("Incident description", { exact: true })
    .fill(`SECOND-${randomUUID().slice(0, 8)}`);
  const second = await reviewDraft(page);
  expect(second.operationId).not.toBe(first.operationId);
});

test("case intake preserves focus and distinguishes empty lifecycle views", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await page.emulateMedia({ forcedColors: "active" });
  await openAuthenticated(page);
  await page.goto("/cases?view=ALL");
  const open = page.getByRole("link", { name: "Open a case", exact: true });
  const executions: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/operation.execute") {
      executions.push(path);
    }
  });
  await open.click();
  const dialog = page.getByRole("dialog", { name: "Open a case" });
  const amount = dialog.getByRole("textbox", { name: "Claimed amount", exact: true });
  const review = dialog.getByRole("button", { name: "Review changes" });
  await expect(dialog.getByText(/ClaimCore assigns its permanent reference/u)).toBeVisible();
  await amount.focus();
  await amount.evaluate((element) => {
    element.scrollIntoView({ block: "center" });
  });
  const amountControl = dialog.locator('.control-box:has(input[name="field:claimedAmount"])');
  await expect(amount).toBeFocused();
  await expect(amountControl).toHaveCSS("outline-style", "solid");
  await expect.poll(() => clippedControlEdges(amountControl, true)).toEqual([]);
  await noHorizontalOverflow(page, "narrow");
  await expect(review).toBeInViewport();
  await amount.fill("invalid");
  await amount.press("Enter");
  await expect(dialog.getByRole("alert").first()).toBeVisible();
  await expect(amount).toHaveAttribute("aria-invalid", "true");
  await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
  const discard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expect(discard).toBeVisible();
  await discard.getByRole("button", { name: "Keep editing", exact: true }).click();
  await expect(amount).toHaveValue("invalid");
  await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
  await discard.getByRole("button", { name: "Discard and leave", exact: true }).click();
  await expect(discard).toHaveCount(0);
  await expect(dialog).toHaveCount(0);
  expect(executions).toEqual([]);

  await open.click();
  const query = `INTAKE-${randomUUID().slice(0, 8)}`;
  await expect(
    dialog.getByText("Leave both blank, or enter both amount and currency."),
  ).toBeVisible();
  await amount.fill("0");
  await dialog.getByRole("combobox", { name: "Claimed currency", exact: true }).fill("EUR");
  await page.getByRole("option", { name: "EUR", exact: true }).click();
  await reviewDraft(page, { "Policy number": query });
  await recordReviewed(page);
  const issued = await page.locator("#case-detail-title").innerText();

  await page.goto(`/cases?view=ALL&q=${query}`);
  await expect(page.getByRole("link", { name: issued, exact: true })).toBeVisible();
  for (const lifecycle of ["ERASED", "VOIDED"]) {
    await page.goto(`/cases?view=ALL&lifecycle=${lifecycle}&q=${query}`);
    const empty = page.locator(".empty-state").filter({ hasText: "No cases match this view." });
    await expect(empty).toBeVisible();
    await expect(page.getByText("There are no cases in your accessible scope.")).toHaveCount(0);
    await expect(empty.getByRole("button", { name: "Clear filters" })).toBeVisible();
  }
});
