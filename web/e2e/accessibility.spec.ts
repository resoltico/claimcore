import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";

import { expect, test, type Page } from "@playwright/test";

import { prepare, startOpen } from "./case-workflow";
import { expectAccessible, login, progress } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const assertNarrowLayout = async (page: Page, stage: "login" | "cases" | "zoom"): Promise<void> => {
  const metrics = await page.evaluate(() => {
    const clientWidth = document.documentElement.clientWidth;
    const offenders = [...document.querySelectorAll("*")]
      .map((element) => ({ element, rect: element.getBoundingClientRect() }))
      .filter(({ rect }) => rect.right > clientWidth + 1)
      .slice(0, 5)
      .map(({ element, rect }) => ({
        tag: element.tagName.toLowerCase(),
        className: typeof element.className === "string" ? element.className : "",
        left: Math.round(rect.left),
        right: Math.round(rect.right),
        width: Math.round(rect.width),
      }));
    return {
      clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      bodyWidth: Math.round(document.body.getBoundingClientRect().width),
      offenders,
    };
  });
  if (metrics.scrollWidth <= metrics.clientWidth + 1) return;
  const safe = metrics.offenders.map(({ tag, className, left, right, width }) => {
    const name = className.replace(/[^a-zA-Z0-9_-]/gu, "").slice(0, 40);
    return { tag, className: name, left, right, width };
  });
  const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"];
  if (engine !== undefined && ["chromium", "firefox", "webkit"].includes(engine)) {
    const report = new URL(`../../artifacts/browser/layout-${engine}.json`, import.meta.url);
    await writeFile(report, `${JSON.stringify({ stage, ...metrics, offenders: safe })}\n`);
  }
  throw new Error(`E2E_NARROW_${stage.toUpperCase()}_OVERFLOW`);
};

const openAccessibleLogin = async (page: Page): Promise<void> => {
  await progress("a11y-body-start");
  await page.setViewportSize({ width: 320, height: 720 });
  await progress("a11y-viewport-ready");
  await page.emulateMedia({ reducedMotion: "reduce", forcedColors: "active" });
  await progress("a11y-media-ready");
  await page.goto("/", { waitUntil: "domcontentloaded", timeout: 10_000 });
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
  await assertNarrowLayout(page, "login");
  await login(page);
  await progress("a11y-cases");
  await expectAccessible(page);
  await assertNarrowLayout(page, "cases");
  await startOpen(page, caseReference);
  await progress("a11y-editor");
  await expectAccessible(page);
  await prepare(page);
  const review = page.getByRole("dialog", { name: "Review prepared operation" });
  await expect(review).toBeVisible();
  await progress("a11y-review");
  await expectAccessible(page);
  await review.getByRole("checkbox").focus();
  await page.keyboard.press("Space");
  await review.getByRole("button", { name: "Submit exact request" }).focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("heading", { name: "Accepted operation", exact: true }),
  ).toBeVisible();
  await progress("a11y-receipt");
  await expectAccessible(page);
  await page.getByRole("button", { name: "Return to case" }).click();
  await page.getByRole("button", { name: caseReference }).click();
  await page.locator(".history-list summary").first().click();
  await progress("a11y-history");
  await expectAccessible(page);
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  await progress("a11y-zoom");
  await assertNarrowLayout(page, "zoom");
  await page.context().clearCookies();
  await page.reload();
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await progress("a11y-expired-session");
  await expectAccessible(page);
});
