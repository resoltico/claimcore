import { expectCaseReadsSettled } from "./case-readiness";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "./fixtures";

import { expectKeyboardContained } from "./localization-support";
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
  await expect(page.locator("#kc-page-title")).toBeVisible();
  await expect(page.locator('input[name="username"]')).toBeVisible();
};

test("keeps published login, editor, review, receipt and history accessible at narrow and zoom-equivalent reflow viewports @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
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
  const review = page.getByRole("dialog", { name: "Open a case", exact: true });
  await expect(review).toBeVisible();
  await progress("a11y-review");
  await expectAccessible(page);
  await expect(review.getByRole("checkbox")).toHaveCount(0);
  await expectKeyboardContained(page, review);
  await review.getByRole("button", { name: "Open a case", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expectCaseReadsSettled(page);
  await progress("a11y-receipt");
  await expectAccessible(page);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  const meters = page.locator(".case-stage-list meter");
  await expect(meters).toHaveCount(4);
  for (const meter of await meters.all()) {
    await expect(meter).toHaveAccessibleName(/.+/u);
    expect(await meter.evaluate((element) => getComputedStyle(element).appearance)).toBe("auto");
  }
  await expect(page.locator(".case-title-line .chip-list")).toContainText("Open");
  const timeline = page.locator(".case-timeline-panel");
  await expect(timeline).toHaveAttribute("aria-busy", "false");
  const facts = timeline.getByRole("tab", { name: "Case facts", exact: true });
  await facts.click();
  await expect(facts).toHaveAttribute("aria-selected", "true");
  await expect(timeline).toHaveAttribute("aria-busy", "false");
  await expect(timeline.locator(".region-timeline > li")).toHaveCount(1);
  await expect(timeline.locator(".region-timeline > li")).toContainText("Case facts recorded");
  await progress("a11y-history");
  await expectAccessible(page);
  // A 640x900 viewport at 200% browser zoom has this CSS viewport; do not inject CSS zoom.
  await page.setViewportSize({ width: 320, height: 450 });
  await progress("a11y-zoom");
  await noHorizontalOverflow(page, "zoom");
  await page.context().clearCookies();
  await page.reload({ waitUntil: "commit" });
  await expect(page.locator("#kc-page-title")).toBeVisible();
  await expect(page.locator('input[name="username"]')).toBeVisible();
  await expect(page.locator("#case-detail-title")).toHaveCount(0);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await progress("a11y-expired-session");
  await expectAccessible(page);
});
