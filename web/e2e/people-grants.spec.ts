import { selectedActor, applyPeople } from "./people-setup";
import type { ResponseFor } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { operationId as mintOperationId } from "../src/operation/operationId";

import { openCase } from "./case-workflow";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  noHorizontalOverflow,
  selectLanguage,
  selectFormat,
  seedPresentation,
  ui,
} from "./localization-support";
const expectPeopleControlsContained = async (page: Page) => {
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
    return {
      panelLeft: panelBounds.left,
      panelRight: panelBounds.right,
      count,
      left,
      right,
    };
  });
  expect(geometry.count).toBeGreaterThan(0);
  expect(geometry.left).toBeGreaterThanOrEqual(geometry.panelLeft - 1);
  expect(geometry.right).toBeLessThanOrEqual(geometry.panelRight + 1);
};
const checkPresentation = async (page: Page, complete: boolean, count: number, name: string) => {
  const view = page.getByRole("dialog");
  await page.setViewportSize({
    width: 320,
    height: 800,
  });
  await expect(
    view.getByRole("heading", {
      name,
      exact: true,
    }),
  ).toBeVisible();
  await noHorizontalOverflow(page, "narrow");
  await expectPeopleControlsContained(page);
  for (const language of ["lv", "ar", "en"] as const) {
    await selectLanguage(page, language);
    await selectFormat(page, "lv-LV");
    await expect(
      view.getByRole("heading", {
        name,
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      view.getByRole("button", {
        name: ui(language, "ui.reloadGrants"),
        exact: true,
      }),
    ).toBeEnabled();
    await expect(view.getByRole("status")).toHaveText(
      ui(language, complete ? "ui.grantsEnd" : "ui.grantsPartial"),
    );
    await expect(view.locator(".table-view tbody > tr")).toHaveCount(count);
    await noHorizontalOverflow(page, "narrow");
    await expectPeopleControlsContained(page);
  }
};
const clickPersonCell = async (page: Page) => {
  const link = page.locator('a[id^="actor-grants-"]').first();
  await link.scrollIntoViewIfNeeded();
  await link.click();
};
test("selected people grants use bounded core pages and survive presentation changes at narrow width", async ({
  page,
}) => {
  const grantRequests: string[] = [];
  page.on("request", (request) => {
    if (request.url().endsWith("/api/people.grants")) {
      grantRequests.push(request.postData() ?? "");
    }
  });
  await seedPresentation(page, "en-XA", "ar-EG");
  await openAuthenticated(page, "⟦Cààsëës⟧");
  const listingResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/people.list",
  );
  await page.getByRole("link").and(page.locator('nav a[href="/people"]')).click();
  const listing = (await (await listingResponse).json()) as ResponseFor<"people.list", "HUMAN">;
  if (listing.outcome.kind !== "SUCCEEDED") {
    throw new Error("Directory read required");
  }
  const content = listing.outcome.document.screen.regions.find(
    (region) => region.role === "RESULTS",
  )?.content;
  if (content?.kind !== "TABLE") {
    throw new Error("Core directory table required");
  }
  const table = page.locator('table[aria-labelledby="people-title"]');
  await expect(table.getByRole("columnheader")).toHaveCount(content.columns.length);
  await expect(table.locator("thead")).toBeVisible();
  const selected = page.locator('a[id^="actor-grants-"]').first();
  await expect(selected).toBeVisible();
  expect(grantRequests).toHaveLength(0);
  const reply = page.waitForResponse((response) => response.url().endsWith("/api/people.grants"));
  await selected.focus();
  await page.keyboard.press("Enter");
  const peopleGrantsReply = (await (await reply).json()) as ResponseFor<"people.grants", "HUMAN">;
  if (
    peopleGrantsReply.representation !== "HUMAN" ||
    peopleGrantsReply.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("Expected a bounded actor grant page.");
  }
  const { document: grantDocument } = peopleGrantsReply.outcome;
  const account = grantDocument.screen.regions
    .flatMap((region) =>
      region.role === "ACCOUNT" && region.content.kind === "ENTRIES" ? region.content.entries : [],
    )
    .find((entry) => entry.key === "actor");
  if (account?.value.kind !== "ACTOR" || account.value.value.displayName.kind !== "NAMED") {
    throw new Error("Expected the admitted grant target to have its authored name.");
  }
  const name = account.value.value.displayName;
  const count = grantDocument.screen.regions.flatMap((region) =>
    region.content.kind === "TABLE" ? region.content.rows : [],
  ).length;
  const view = page.getByRole("dialog");
  expect(await view.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await expect(view.locator(".ui-dialog-title")).toHaveAccessibleName(name.value);
  await expect(view.locator(".table-view tbody > tr")).toHaveCount(count);
  await checkPresentation(page, grantDocument.nextCursor === null, count, name.value);
  expect(grantRequests).toHaveLength(1);
  await expectAccessible(page);
  await selectLanguage(page, "en");
  await view
    .locator(".ui-dialog-actions")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .click();
  await expect(view).toHaveCount(0);
  await expect(selected).toBeFocused();
  for (const width of [1280, 320]) {
    await page.setViewportSize({
      width,
      height: 900,
    });
    await clickPersonCell(page);
    expect(await view.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    await expect(view.locator(".ui-dialog-title")).toHaveAccessibleName(name.value);
    await view
      .locator(".ui-dialog-actions")
      .getByRole("button", {
        name: "Close",
        exact: true,
      })
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
  const target = {
    kind: "HUMAN",
    issuer: "https://historical-issuer.example/",
    subject,
  } as const;
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.register",
    binding: { kind: "PEOPLE" },
    inputs: {
      "input:principal.kind": target.kind,
      "input:principal.issuer": target.issuer,
      "input:principal.subject": target.subject,
      "input:profile.displayName": subject,
      "input:profile.contact": "historical-browser-only@example.test",
    },
  });
  const actorId = await selectedActor(page, target);
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.setProfile",
    binding: { kind: "PEOPLE", actorId },
    inputs: {
      "input:profile.displayName": subject,
      "input:profile.contact": "current-browser-only@example.test",
    },
  });
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId },
    inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
  });
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId },
    inputs: {
      "input:role": "CASE_HANDLER",
      "input:scope": "CASE",
      "field:caseReference": reference,
    },
  });
  await page.goto("/people");
  await page.locator("#people-search").fill(subject);
  await page.locator(".directory-filters button[type='submit']").click();
  const row = page.locator(".table-view tbody > tr").filter({
    hasText: subject,
  });
  await expect(row).toHaveCount(1);
  await row.getByRole("link").click();
  const profile = page.getByRole("dialog");
  const roles = profile.locator(".table-view tbody > tr");
  await expect(roles).toHaveCount(2);
  await expect(
    roles.filter({
      hasText: "Case reader",
    }),
  ).toContainText("All cases");
  const scoped = roles.filter({
    hasText: "Case handler",
  });
  await expect(scoped).toContainText("One case");
  await expect(scoped).toContainText(reference);
  await page.goto("/people-history");
  const historical = page.locator(".people-history .region-timeline > li").filter({
    hasText: "historical-browser-only@example.test",
  });
  await expect(historical).toHaveCount(1);
  await expect(historical).toContainText("historical-browser-only@example.test");
  const details = page.locator('.people-history details[data-region="DETAILS"]');
  await details.locator(":scope > summary").click();
  const issuer = details.locator('[data-entry$=".issuer"]').filter({
    hasText: "https://historical-issuer.example/",
  });
  await expect(issuer).toHaveCount(1);
  const key = await issuer.getAttribute("data-entry");
  const sequence = key?.slice(0, -".issuer".length);
  if (sequence === undefined) {
    throw Error("Historical metadata requires its sequence key");
  }
  await expect(details.locator(`[data-entry="${sequence}.subject"]`)).toContainText(subject);
});
