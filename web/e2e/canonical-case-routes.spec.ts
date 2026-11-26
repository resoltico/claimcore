import { type ResponseFor, type CaseSelector } from "@contracts/client";
import { goToView } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "./fixtures";
import { openCase } from "./case-workflow";
import { browserRequest, openAuthenticated, sessionToken } from "./session-helpers";
import { casePath, referencePath } from "../src/routing/routes";
import { workRequest } from "./work-workflow";
import { operationId } from "../src/operation/operationId";
import { geometryFailures } from "./design-geometry";
const readCase = async (page: Page, target: CaseSelector) => {
  const response = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      target,
    }),
  });
  const caseGetReply = response.payload as ResponseFor<"case.get", "DATA">;
  if (
    response.status !== 200 ||
    caseGetReply.representation !== "DATA" ||
    caseGetReply.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("The independently selected case must be readable");
  }
  return caseGetReply.outcome.data;
};
test("canonical Case ID links and explicit reference lookup select different actual cases", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `CANON-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await page.waitForLoadState("networkidle");
  const first = await readCase(page, {
    kind: "REFERENCE",
    caseReference: reference,
  });
  await expect(page).toHaveURL((url) => url.pathname === `/cases/id/${first.caseId}`);
  await goToView(page, "cases");
  const alias = `Case-${first.caseId.slice(-12)}`;
  await openCase(page, alias);
  await page.waitForLoadState("networkidle");
  const second = await readCase(page, {
    kind: "REFERENCE",
    caseReference: alias.toLowerCase(),
  });
  expect(second.caseId).not.toBe(first.caseId);
  expect(second.caseReference).toBe(alias);
  await page.goto(casePath(first.caseId), {
    waitUntil: "commit",
  });
  await expect(page.locator("#case-detail-title")).toHaveText(first.caseReference);
  await page.goto(referencePath(alias), {
    waitUntil: "commit",
  });
  await expect(page.locator("#case-detail-title")).toHaveText(second.caseReference);
  await page.goto(`/cases?view=ALL&q=${encodeURIComponent(second.caseReference)}`);
  const link = page.locator(".table-view").getByRole("link", {
    name: second.caseReference,
    exact: true,
  });
  await expect(link).toHaveAttribute("href", `/cases/id/${second.caseId}`);
  const request = page.waitForRequest((value) => new URL(value.url()).pathname === "/api/case.get");
  await link.click();
  expect((await request).postDataJSON()).toEqual({
    target: {
      kind: "ID",
      caseId: second.caseId,
    },
  });
  await expect(page.locator("#case-detail-title")).toHaveText(second.caseReference);
});
test("case inspection restores the live work query, selected row focus and scroll without history payloads", async ({
  page,
}) => {
  await openAuthenticated(page);
  const marker = `INSPECT-${randomUUID().slice(0, 8)}`;
  for (let index = 0; index < 8; index += 1) {
    const result = await workRequest(page, "operation.execute", {
      operationId: operationId(),
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: { "field:caseReference": `${marker}-${index}` },
    });
    expect(result.outcome.kind).toBe("ACCEPTED");
  }
  await page.setViewportSize({
    width: 390,
    height: 800,
  });
  await page
    .getByRole("tab", {
      name: /^All(?:\s|$)/u,
    })
    .click();
  await expect(page).toHaveURL((url) => url.searchParams.get("view") === "ALL");
  const filter = page.getByRole("searchbox", { name: "Filter this view" });
  await filter.fill(marker);
  await filter.press("Enter");
  await expect(page).toHaveURL((url) => url.searchParams.get("q") === marker);
  await page.waitForLoadState("networkidle");
  const links = page.locator('a[id^="case-"][href^="/cases/id/"]');
  await expect(links).toHaveCount(8);
  const selected = links.last();
  await selected.scrollIntoViewIfNeeded();
  const position = await page.evaluate(() => scrollY);
  expect(position).toBeGreaterThan(0);
  const id = await selected.getAttribute("id");
  expect(id).toMatch(/^case-[0-9a-f-]{36}$/u);
  const url = page.url();
  const reference = (await selected.textContent())?.trim();
  expect(reference).toBeTruthy();
  await selected.click();
  await expect(page.locator(".phone-app-bar")).toContainText(reference!);
  await page
    .getByRole("navigation", { name: "Breadcrumb" })
    .getByRole("link", { name: "Cases", exact: true })
    .click();
  await expect(page).toHaveURL(url);
  await expect(page.locator(`[id="${id}"]`)).toBeFocused();
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(position);
  expect(await page.evaluate((): unknown => history.state)).toEqual({
    claimcoreNavigation: {
      group: expect.any(String),
      index: expect.any(Number),
      key: expect.any(String),
    },
  });
});

test("phone Cases cards fit a maximal accepted reference @engine", async ({ page }) => {
  await openAuthenticated(page);
  const reference = "W".repeat(32);
  const result = await workRequest(page, "operation.execute", {
    operationId: operationId(),
    task: "case.open",
    binding: { kind: "CASE", expectedRevision: 0 },
    inputs: { "field:caseReference": reference },
  });
  expect(result.outcome.kind).toBe("ACCEPTED");
  await page.goto(`/cases?view=ALL&q=${reference}`);
  const list = page.locator(".worklist");
  await expect(list).toHaveAttribute("aria-busy", "false");
  const link = list.locator(".data-table").getByRole("link", { name: reference, exact: true });
  await expect(link).toHaveCount(1);

  for (const width of [390, 513, 514, 560, 600, 639]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(link).toHaveText(reference);
    const failures = await geometryFailures(page);
    expect(
      failures.filter((id) => id === "G4" || id === "G9"),
      `Maximal case reference at ${width}px`,
    ).toEqual([]);
  }
});

test("historical exact Find admits a case-folded reference and restores shortcut focus", async ({
  page,
}) => {
  await openAuthenticated(page);
  const earlier = `OLD-${randomUUID().slice(0, 8)}`;
  const current = `NEW-${randomUUID().slice(0, 8)}`;
  await openCase(page, earlier);
  const changed = await workRequest(page, "operation.execute", {
    operationId: operationId(),
    task: "case.changeReference",
    binding: {
      kind: "CASE",
      target: { kind: "REFERENCE", caseReference: earlier },
      expectedRevision: 1,
    },
    inputs: { "input:newReference": current },
  });
  expect(changed.outcome.kind).toBe("ACCEPTED");
  const selectedReference = earlier.toLowerCase();
  const expectedHref = referencePath(selectedReference);
  await page.goto(expectedHref, { waitUntil: "commit" });
  await expect(page.locator("#case-detail-title")).toHaveText(current);
  await expect(page.getByRole("navigation", { name: "Breadcrumb" })).toContainText(
    selectedReference,
  );

  await page.goto("/cases?view=ALL");
  const before = page.url();
  const trigger = page.locator("#workspace-find");
  await trigger.press("Enter");
  const search = page.getByRole("dialog").getByRole("combobox", { name: "Find a case" });
  await expect(search).toBeFocused();
  await search.fill(earlier);
  const selected = page.waitForRequest((request) => {
    if (new URL(request.url()).pathname !== "/api/case.get") {
      return false;
    }
    return (request.postDataJSON() as { target?: CaseSelector }).target?.kind === "REFERENCE";
  });
  await search.press("Enter");
  expect((await selected).postDataJSON()).toEqual({
    target: { kind: "REFERENCE", caseReference: earlier },
  });
  await expect(page).toHaveURL(/\/cases\/id\//u);
  await expect(page.locator("#case-detail-title")).toHaveText(current);
  await page.goBack();
  await expect(page).toHaveURL(before);
  await expect(trigger).toBeFocused();
});
