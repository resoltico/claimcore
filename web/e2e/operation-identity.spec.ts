import { randomUUID } from "node:crypto";

import { expect, test } from "./fixtures";

import { recordReviewed, reviewDraft, startOpen } from "./case-workflow";
import { clippedControlEdges, noHorizontalOverflow } from "./layout-support";
import { openAuthenticated } from "./session-helpers";

test("rotates a exposed OPEN identity when only its target reference changes", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `FIRST-${randomUUID().slice(0, 8)}`);
  const first = await reviewDraft(page);
  await page.getByRole("button", { name: "Return to editing" }).click();
  await expect(page.getByRole("dialog", { name: "Open a case" })).toBeVisible();
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toHaveCount(0);
  await expect(page.getByLabel("Case reference", { exact: true })).toBeVisible();
  await page
    .getByLabel("Case reference", { exact: true })
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
  const custom = dialog.getByRole("checkbox", { name: "Use a custom case reference" });
  await expect(custom).toBeEnabled();
  await custom.check();
  const reference = dialog.getByRole("textbox", { name: "Case reference", exact: true });
  const review = dialog.getByRole("button", { name: "Review changes" });
  await expect(reference).toHaveAccessibleDescription(
    /stays reserved even after correction or erasure/u,
  );
  await reference.focus();
  await reference.evaluate((element) => {
    element.scrollIntoView({ block: "center" });
  });
  await expect(reference).toHaveCSS("outline-style", "solid");
  await expect.poll(() => clippedControlEdges(reference, true)).toEqual([]);
  await noHorizontalOverflow(page, "narrow");
  await expect(review).toBeInViewport();
  await reference.fill("RESTORE");
  await reference.fill("");
  await review.click();
  await expect(dialog.getByRole("alert").first()).toBeVisible();
  await expect(reference).toBeFocused();
  await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
  const discard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expect(discard).toBeVisible();
  await discard.getByRole("button", { name: "Keep editing", exact: true }).click();
  await expect(custom).toBeChecked();
  await expect(reference).toHaveValue("");
  await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
  await discard.getByRole("button", { name: "Discard and leave", exact: true }).click();
  await expect(discard).toHaveCount(0);
  await expect(dialog).toHaveCount(0);
  expect(executions).toEqual([]);

  await open.click();
  await custom.check();
  const query = `INTAKE-${randomUUID().slice(0, 8)}`;
  await reference.fill(`${query}-ACTIVE`);
  await expect(
    dialog.getByText("Leave both blank, or enter both amount and currency."),
  ).toBeVisible();
  await dialog.getByRole("textbox", { name: "Claimed amount", exact: true }).fill("0");
  await dialog.getByRole("combobox", { name: "Claimed currency", exact: true }).fill("EUR");
  await page.getByRole("option", { name: "EUR", exact: true }).click();
  await reviewDraft(page, { "Policy number": "SYNTHETIC-INTAKE" });
  await recordReviewed(page);

  await page.goto(`/cases?view=ALL&q=${query}`);
  await expect(page.getByRole("link", { name: `${query}-ACTIVE`, exact: true })).toBeVisible();
  for (const lifecycle of ["ERASED", "VOIDED"]) {
    await page.goto(`/cases?view=ALL&lifecycle=${lifecycle}&q=${query}`);
    const empty = page.locator(".empty-state").filter({ hasText: "No cases match this view." });
    await expect(empty).toBeVisible();
    await expect(page.getByText("There are no cases in your accessible scope.")).toHaveCount(0);
    await expect(empty.getByRole("button", { name: "Clear filters" })).toBeVisible();
  }
});
