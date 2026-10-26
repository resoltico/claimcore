import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  noHorizontalOverflow,
  selectLanguage,
  selectFormat,
  seedPresentation,
  ui,
} from "./localization-support";

const expectAuthorityControlsContained = async (page: Page) => {
  const panel = page.locator(".authority-panel");
  await expect(panel).toHaveCount(1);
  const geometry = await panel.evaluate((element) => {
    const panelBounds = element.getBoundingClientRect();
    let count = 0;
    let left = panelBounds.right;
    let right = panelBounds.left;
    for (const control of element.querySelectorAll('input:not([type="checkbox"]), select')) {
      const bounds = control.getBoundingClientRect();
      if (
        control.getClientRects().length === 0 ||
        bounds.width <= 0 ||
        bounds.height <= 0 ||
        getComputedStyle(control).visibility !== "visible"
      ) {
        continue;
      }
      count += 1;
      left = Math.min(left, bounds.left);
      right = Math.max(right, bounds.right);
    }
    return { panelLeft: panelBounds.left, panelRight: panelBounds.right, count, left, right };
  });
  expect(geometry.count).toBeGreaterThan(0);
  expect(geometry.left).toBeGreaterThanOrEqual(geometry.panelLeft - 1);
  expect(geometry.right).toBeLessThanOrEqual(geometry.panelRight + 1);
};

const checkPresentation = async (page: Page, complete: boolean) => {
  const view = page.locator('section[aria-labelledby="actor-grants-title"]');
  await page.setViewportSize({ width: 320, height: 800 });
  await expect(view.getByRole("heading")).toBeVisible();
  await noHorizontalOverflow(page, "narrow");
  await expectAuthorityControlsContained(page);
  for (const language of ["lv", "ar", "en"] as const) {
    await selectLanguage(page, language);
    await selectFormat(page, "lv-LV");
    await expect(
      view.getByRole("heading", { name: ui(language, "ui.actorGrants"), exact: true }),
    ).toBeVisible();
    await expect(
      view.getByRole("button", { name: ui(language, "ui.reloadGrants"), exact: true }),
    ).toBeEnabled();
    await expect(view.getByRole("status")).toHaveText(
      ui(language, complete ? "ui.grantsEnd" : "ui.grantsPartial"),
    );
    await noHorizontalOverflow(page, "narrow");
    await expectAuthorityControlsContained(page);
  }
};

test("selected authority grants use bounded core pages and survive presentation changes at narrow width", async ({
  page,
}) => {
  const grantRequests: string[] = [];
  page.on("request", (request) => {
    if (request.url().endsWith("/api/authority.grants")) {
      grantRequests.push(request.postData() ?? "");
    }
  });
  await seedPresentation(page, "en-XA", "ar-EG");
  await openAuthenticated(page, "⟦Cààsëës⟧");
  await page.locator("nav button").last().click();
  const selected = page.locator('button[id^="actor-grants-"]').first();
  await expect(selected).toBeVisible();
  expect(grantRequests).toHaveLength(0);
  const reply = page.waitForResponse((response) =>
    response.url().endsWith("/api/authority.grants"),
  );
  await selected.click();
  const value: unknown = await (await reply).json();
  expect(isResponse("authority.grants", value)).toBe(true);
  if (!isResponse("authority.grants", value) || value.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected a bounded actor grant page.");
  }
  const view = page.locator('section[aria-labelledby="actor-grants-title"]');
  await expect(view.getByRole("heading")).toBeFocused();
  await expect(view.getByText(value.outcome.data.actor.actorId, { exact: true })).toBeVisible();
  await expect(view.locator("li")).toHaveCount(value.outcome.data.items.length);
  await checkPresentation(page, value.outcome.data.nextCursor === null);
  expect(grantRequests).toHaveLength(1);
  await expectAccessible(page);
  await selectLanguage(page, "en");
  await view.getByRole("button", { name: "Back to actors", exact: true }).click();
  await expect(view).toHaveCount(0);
  await expect(selected).toBeFocused();
});
