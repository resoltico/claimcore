import { randomUUID } from "node:crypto";
import type { RequestByEndpoint } from "@contracts/client";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { applyAuthority } from "./authority-setup";
import { openCase } from "./case-workflow";
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
  const panel = page.getByRole("dialog");
  await expect(panel).toHaveCount(1);
  const geometry = await panel.evaluate((element) => {
    const panelBounds = element.getBoundingClientRect();
    let count = 0;
    let left = panelBounds.right;
    let right = panelBounds.left;
    for (const control of element.querySelectorAll(
      'input:not([type="checkbox"]), button, a[href], summary',
    )) {
      const bounds = control.getBoundingClientRect();
      if (
        control.getClientRects().length === 0 ||
        bounds.width <= 0 ||
        bounds.height <= 0 ||
        getComputedStyle(control).visibility !== "visible"
      ) {
        continue;
      }
      const scroll = control.closest(".table-view");
      const viewport = scroll?.getBoundingClientRect();
      const visibleLeft =
        viewport === undefined ? bounds.left : Math.max(bounds.left, viewport.left);
      const visibleRight =
        viewport === undefined ? bounds.right : Math.min(bounds.right, viewport.right);
      if (visibleRight <= visibleLeft) {
        continue;
      }
      count += 1;
      left = Math.min(left, visibleLeft);
      right = Math.max(right, visibleRight);
    }
    return { panelLeft: panelBounds.left, panelRight: panelBounds.right, count, left, right };
  });
  expect(geometry.count).toBeGreaterThan(0);
  expect(geometry.left).toBeGreaterThanOrEqual(geometry.panelLeft - 1);
  expect(geometry.right).toBeLessThanOrEqual(geometry.panelRight + 1);
};

const checkPresentation = async (page: Page, complete: boolean, count: number, name: string) => {
  const view = page.getByRole("dialog");
  await page.setViewportSize({ width: 320, height: 800 });
  await expect(view.getByRole("heading", { name, exact: true })).toBeVisible();
  await noHorizontalOverflow(page, "narrow");
  await expectAuthorityControlsContained(page);
  for (const language of ["lv", "ar", "en"] as const) {
    await selectLanguage(page, language);
    await selectFormat(page, "lv-LV");
    await expect(view.getByRole("heading", { name, exact: true })).toBeVisible();
    await expect(
      view.getByRole("button", { name: ui(language, "ui.reloadGrants"), exact: true }),
    ).toBeEnabled();
    await expect(view.getByRole("status")).toHaveText(
      ui(language, complete ? "ui.grantsEnd" : "ui.grantsPartial"),
    );
    await expect(view.locator(".table-view tbody > tr")).toHaveCount(count);
    await noHorizontalOverflow(page, "narrow");
    await expectAuthorityControlsContained(page);
  }
};

const clickPersonCell = async (page: Page) => {
  const link = page.locator('a[id^="actor-grants-"]').first();
  await link.scrollIntoViewIfNeeded();
  await link.click();
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
  await page.getByRole("link").and(page.locator('nav a[href="/people"]')).click();
  const table = page.locator(".table-view table").first();
  await expect(table.getByRole("columnheader")).toHaveCount(9);
  await expect(table.locator("thead")).toBeVisible();
  const selected = page.locator('a[id^="actor-grants-"]').first();
  await expect(selected).toBeVisible();
  expect(grantRequests).toHaveLength(0);
  const reply = page.waitForResponse((response) =>
    response.url().endsWith("/api/authority.grants"),
  );
  await selected.focus();
  await page.keyboard.press("Enter");
  const value: unknown = await (await reply).json();
  expect(isResponse("authority.grants", value)).toBe(true);
  if (!isResponse("authority.grants", value) || value.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected a bounded actor grant page.");
  }
  const { data } = value.outcome;
  const name = data.actor.actor.displayName;
  if (name.kind !== "NAMED") {
    throw new Error("Expected the admitted grant target to have its authored name.");
  }
  const view = page.getByRole("dialog");
  expect(await view.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await expect(view.locator(".ui-dialog-title")).toHaveText(name.value);
  await expect(view.locator(".table-view tbody > tr")).toHaveCount(data.items.length);
  await checkPresentation(page, data.nextCursor === null, data.items.length, name.value);
  expect(grantRequests).toHaveLength(1);
  await expectAccessible(page);
  await selectLanguage(page, "en");
  await view
    .locator(".ui-dialog-actions")
    .getByRole("button", { name: "Close", exact: true })
    .click();
  await expect(view).toHaveCount(0);
  await expect(selected).toBeFocused();
  for (const width of [1280, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await clickPersonCell(page);
    expect(await view.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    await expect(view.locator(".ui-dialog-title")).toHaveText(name.value);
    await view
      .locator(".ui-dialog-actions")
      .getByRole("button", { name: "Close", exact: true })
      .click();
    await expect(selected).toBeFocused();
  }
});

test("preserves historical profile contact and distinguishes global and case role scopes", async ({
  page,
}) => {
  await openAuthenticated(page);
  const subject = `AUDIT-VIEW-${randomUUID()}`;
  const reference = `ROLE-VIEW-${randomUUID()}`;
  await openCase(page, reference);
  const target = { kind: "HUMAN", issuer: "https://historical-issuer.example/", subject } as const;
  const apply = (
    act: Extract<RequestByEndpoint["operation.execute"], { kind: "AUTHORITY" }>["act"],
  ) => applyAuthority(page, { kind: "AUTHORITY", operationId: mintOperationId(), target, act });
  await apply({
    kind: "REGISTER",
    profile: { displayName: subject, contact: "historical-browser-only@example.test" },
  });
  await apply({
    kind: "SET_PROFILE",
    profile: { displayName: subject, contact: "current-browser-only@example.test" },
  });
  await apply({ kind: "GRANT", role: "CASE_READER", scope: { kind: "INSTALLATION" } });
  await apply({
    kind: "GRANT",
    role: "CASE_HANDLER",
    scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: reference } },
  });
  await page.goto("/people");
  await page.locator("#people-search").fill(subject);
  await page.locator(".directory-filters button[type='submit']").click();
  const row = page.locator(".table-view tbody > tr").filter({ hasText: subject });
  await expect(row).toHaveCount(1);
  await row.getByRole("link").click();
  const profile = page.getByRole("dialog");
  const roles = profile.locator(".table-view tbody > tr");
  await expect(roles).toHaveCount(2);
  await expect(roles.filter({ hasText: "Case reader" })).toContainText("All cases");
  const scoped = roles.filter({ hasText: "Case handler" });
  await expect(scoped).toContainText("One case");
  await expect(scoped).toContainText(reference);
  await page.goto("/authority-history");
  const historical = page
    .locator(".authority-history .region-timeline > li")
    .filter({ hasText: "historical-browser-only@example.test" });
  await expect(historical).toHaveCount(1);
  await expect(historical).toContainText("historical-browser-only@example.test");
  const details = page.locator('.authority-history details[data-region="DETAILS"]');
  await details.locator(":scope > summary").click();
  const issuer = details
    .locator('[data-entry$=".issuer"]')
    .filter({ hasText: "https://historical-issuer.example/" });
  await expect(issuer).toHaveCount(1);
  const key = await issuer.getAttribute("data-entry");
  const sequence = key?.slice(0, -".issuer".length);
  if (sequence === undefined) {
    throw Error("Historical metadata requires its sequence key");
  }
  await expect(details.locator(`[data-entry="${sequence}.subject"]`)).toContainText(subject);
});
