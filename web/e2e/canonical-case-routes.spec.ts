import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import type { CaseSelector } from "@contracts/client";
import { openCase } from "./case-workflow";
import { browserRequest, openAuthenticated, sessionToken } from "./session-helpers";
import { isResponse } from "../src/protocol/client";
import { casePath, referencePath } from "../src/routing/routes";

const readCase = async (page: Page, target: CaseSelector) => {
  const response = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ target }),
  });
  if (
    response.status !== 200 ||
    !isResponse("case.get", response.payload) ||
    response.payload.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("The independently selected case must be readable");
  }
  return response.payload.outcome.data;
};
test("canonical CaseId links and explicit UUID-shaped reference lookup select different actual cases", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `CANON-${randomUUID()}`;
  await openCase(page, reference);
  await page.waitForLoadState("networkidle");
  const first = await readCase(page, { kind: "REFERENCE", caseReference: reference });
  await expect(page).toHaveURL((url) => url.pathname === `/cases/id/${first.caseId}`);
  await page.locator('.workspace-header nav a[href="/cases"]').click();
  await openCase(page, first.caseId);
  await page.waitForLoadState("networkidle");
  const second = await readCase(page, { kind: "REFERENCE", caseReference: first.caseId });
  expect(second.caseId).not.toBe(first.caseId);
  expect(second.caseReference.toUpperCase()).toBe(first.caseId.toUpperCase());
  await page.goto(casePath(first.caseId), { waitUntil: "commit" });
  await expect(page.locator("#case-detail-title")).toHaveText(first.caseReference);
  await page.goto(referencePath(first.caseId), { waitUntil: "commit" });
  await expect(page.locator("#case-detail-title")).toHaveText(second.caseReference);
  await page.locator('.workspace-header nav a[href="/cases"]').click();
  const search = page.getByRole("searchbox");
  await search.fill(second.caseReference);
  await search.press("Enter");
  const link = page.getByRole("link", { name: second.caseReference, exact: true });
  await expect(link).toHaveAttribute("href", `/cases/id/${second.caseId}`);
  const request = page.waitForRequest((value) => new URL(value.url()).pathname === "/api/case.get");
  await link.click();
  expect((await request).postDataJSON()).toEqual({ target: { kind: "ID", caseId: second.caseId } });
  await expect(page.locator("#case-detail-title")).toHaveText(second.caseReference);
});

test("case inspection restores the live work query, selected row focus and scroll without history payloads", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.setViewportSize({ width: 390, height: 800 });
  await page.getByRole("button", { name: "All accessible", exact: true }).click();
  await page.waitForLoadState("networkidle");
  const rows = page.locator(".worklist-row");
  expect(await rows.count()).toBeGreaterThan(1);
  const selected = rows.last().getByRole("link");
  await selected.scrollIntoViewIfNeeded();
  const position = await page.evaluate(() => scrollY);
  expect(position).toBeGreaterThan(0);
  const id = await selected.getAttribute("id");
  expect(id).toMatch(/^case-[0-9a-f-]{36}$/u);
  const url = page.url();
  await selected.click();
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await page.getByRole("button", { name: "Back to cases", exact: true }).click();
  await expect(page).toHaveURL(url);
  await expect(page.locator(`[id="${id}"]`)).toBeFocused();
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(position);
  expect(await page.evaluate((): unknown => history.state)).toBeNull();
});
