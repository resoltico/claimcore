import { randomUUID } from "node:crypto";

import { expect, test } from "./fixtures";

import { recordReviewed, reviewDraft, startOpen } from "./case-workflow";
import { clippedControlEdges, noHorizontalOverflow } from "./layout-support";
import { openAuthenticated } from "./session-helpers";

test("rotates a exposed OPEN identity when only its target reference changes", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `FIRST-${randomUUID()}`);
  const first = await reviewDraft(page);
  await page.getByRole("button", { name: "Return to editing" }).click();
  await expect(page.getByRole("dialog", { name: "Open a case" })).toBeVisible();
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toHaveCount(0);
  await expect(page.getByLabel("Case reference", { exact: true })).toBeVisible();
  await page.getByLabel("Case reference", { exact: true }).fill(`SECOND-${randomUUID()}`);
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
  const drawer = page.getByRole("dialog", { name: "Open a case" });
  const reference = drawer.getByRole("textbox", { name: "Case reference", exact: true });
  const review = drawer.getByRole("button", { name: "Review changes" });
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
  await expect(drawer.getByRole("alert").first()).toBeVisible();
  await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
  await expect(drawer).toHaveCount(0);
  expect(executions).toEqual([]);

  await open.click();
  const query = `INTAKE-${randomUUID()}`;
  await reference.fill(`${query}-ACTIVE`);
  await expect(
    drawer.getByText("Leave both blank, or enter both amount and currency."),
  ).toBeVisible();
  await drawer.getByRole("textbox", { name: "Claimed amount", exact: true }).fill("0");
  await drawer.getByRole("combobox", { name: "Claimed currency", exact: true }).fill("EUR");
  await page.getByRole("option", { name: "EUR", exact: true }).click();
  await reviewDraft(page, { "Policy number": "SYNTHETIC-INTAKE" });
  await recordReviewed(page);

  for (const lifecycle of ["ERASED", "VOIDED"]) {
    await page.goto(`/cases?view=ALL&lifecycle=${lifecycle}&q=${query}`);
    await expect(page.getByText("No cases match this view.")).toBeVisible();
    await expect(page.getByText("There are no cases in your accessible scope.")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Clear filters" })).toBeVisible();
  }
});
