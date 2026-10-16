import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

import { noHorizontalOverflow } from "./layout-support";
import { reviewDraft, startOpen } from "./case-workflow";
import { expectAccessible, login, progress } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const openAccessibleLogin = async (page: Page): Promise<void> => {
  await progress("a11y-body-start");
  await page.setViewportSize({ width: 320, height: 720 });
  await progress("a11y-viewport-ready");
  await page.emulateMedia({ reducedMotion: "reduce", forcedColors: "active" });
  await progress("a11y-media-ready");
  await page.goto("/", { waitUntil: "commit", timeout: 10_000 });
  await progress("a11y-document-ready");
  await expect(page.getByRole("heading", { name: "ClaimCore", exact: true })).toBeVisible();
};

test("keeps published login, editor, review, receipt and history accessible at narrow and zoomed viewports", async ({
  page,
}) => {
  const caseReference = `A11Y-${randomUUID()}`;
  await openAccessibleLogin(page);
  await progress("a11y-login");
  await expectAccessible(page);
  await noHorizontalOverflow(page, "login");
  await login(page);
  await progress("a11y-cases");
  await expectAccessible(page);
  await noHorizontalOverflow(page, "cases");
  await startOpen(page, caseReference);
  await progress("a11y-editor");
  await expectAccessible(page);
  await reviewDraft(page);
  const review = page.getByRole("dialog", { name: "Review changes" });
  await expect(review).toBeVisible();
  await progress("a11y-review");
  await expectAccessible(page);
  await review.getByRole("checkbox").focus();
  await page.keyboard.press("Space");
  await review.getByRole("button", { name: "Record changes" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Recorded change", exact: true })).toBeVisible();
  await progress("a11y-receipt");
  await expectAccessible(page);
  await page.getByRole("button", { name: "Return to case" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await page.getByLabel("History detail", { exact: true }).selectOption("FULL");
  await page.locator(".history-list summary").first().click();
  await progress("a11y-history");
  await expectAccessible(page);
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  await progress("a11y-zoom");
  await noHorizontalOverflow(page, "zoom");
  await page.context().clearCookies();
  await page.reload({ waitUntil: "commit" });
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await progress("a11y-expired-session");
  await expectAccessible(page);
});
