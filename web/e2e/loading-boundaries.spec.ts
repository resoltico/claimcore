import { randomUUID } from "node:crypto";
import { startOpen, reviewDraft } from "./case-workflow";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage } from "./localization-support";

const accessModule = /\/assets\/workspace~AuthorityPanel-[^/]+\.js$/u;
const latvianModule = /\/assets\/language-lv-[^/]+\.js$/u;

const reloadDocument = async (page: Page) => {
  const before = await page.evaluate(() => window.performance.timeOrigin);
  await Promise.all([
    page.waitForEvent("framenavigated", { predicate: (frame) => frame === page.mainFrame() }),
    page.getByRole("button", { name: "Reload interface", exact: true }).click(),
  ]);
  await page.waitForLoadState("domcontentloaded");
  await expect.poll(() => page.evaluate(() => window.performance.timeOrigin)).not.toBe(before);
};

test("initial Cases loads only its language and required modules; public bytes and private responses have separate cache policies", async ({
  page,
}) => {
  const assets: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/assets/")) {
      assets.push(path);
    }
  });
  await openAuthenticated(page);
  await page.waitForLoadState("networkidle");
  expect(assets.some((path) => path.includes("language-en-"))).toBe(true);
  expect(assets.some((path) => /language-(?:lv|ar)-/u.test(path))).toBe(false);
  expect(assets.some((path) => /workspace~OperationEditor~AuthorityPanel-/u.test(path))).toBe(
    false,
  );
  const asset = assets.find((path) => path.endsWith(".js"));
  if (asset === undefined) {
    throw new Error("The published interface must request JavaScript");
  }
  const publicResponse = await page.request.get(asset);
  expect(publicResponse.status()).toBe(200);
  expect(publicResponse.headers()["cache-control"]).toBe("public, max-age=31536000, immutable");
  expect(publicResponse.headers()["set-cookie"]).toBeUndefined();
  const { etag } = publicResponse.headers();
  expect(etag).toMatch(/^"[a-f0-9]{64}"$/u);
  const retained = await page.request.get(asset, { headers: { "If-None-Match": etag! } });
  expect(retained.status()).toBe(304);
  for (const path of ["/cases", "/api/session", "/assets/missing-build.js"]) {
    const response = await page.request.get(path);
    expect(response.headers()["cache-control"]).toBe("no-store, max-age=0");
  }
});

test("a missing language chunk leaves the working pair and direction intact and supports explicit recovery", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.route(latvianModule, (route) =>
    route.fulfill({
      status: 404,
      contentType: "text/plain",
      headers: { "Cache-Control": "no-store" },
      body: "SYNTHETIC_MISSING_LANGUAGE_CHUNK",
    }),
  );
  await page.locator("[data-settings-button]").click();
  await page.getByRole("combobox", { name: "Interface language" }).selectOption("lv");
  await expect(page.getByRole("alert")).toContainText("The language could not be loaded");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  await page.unroute(latvianModule);
  // Browsers may retain a failed module fetch in the document's module map.
  // Explicit reload is available and keeps persisted preferences at the last admitted pair.
  await reloadDocument(page);
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await selectLanguage(page, "lv");
  await expect(page.locator("html")).toHaveAttribute("lang", "lv");
});

test("a missing view chunk leaves the workspace available and explicit reload installs the current view", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.route(accessModule, (route) =>
    route.fulfill({
      status: 404,
      contentType: "text/plain",
      headers: { "Cache-Control": "no-store" },
      body: "SYNTHETIC_MISSING_VIEW_CHUNK",
    }),
  );
  await page.locator('.workspace-header nav a[href="/authority"]').click();
  await expect(page.getByRole("alert")).toContainText("This view could not be loaded");
  await expect(page.locator('.workspace-header nav a[href="/operations"]')).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry loading", exact: true })).toBeVisible();
  await page.unroute(accessModule);
  await reloadDocument(page);
  await expect(page.getByRole("heading", { name: "Access", exact: true })).toBeVisible();
  await expect(page.locator(".authority-panel")).toBeVisible();
  await expect(page.locator('[name="target.subject"]')).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test("offline language activation and declined update reload preserve raw entry, review identity and confirmation", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `CHUNK-DRAFT-${randomUUID()}`);
  const amount = page.locator('input[name="act.values.claimedAmount"]');
  await amount.fill("1,234");
  const executions: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions.push(request.postData() ?? "");
    }
  });
  await page.waitForLoadState("networkidle");
  await page.context().setOffline(true);
  await page.locator("[data-settings-button]").click();
  await page.getByRole("combobox", { name: "Interface language" }).selectOption("lv");
  await expect(page.getByRole("alert")).toContainText("The language could not be loaded");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  await page.getByRole("button", { name: "Reload interface", exact: true }).click();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Keep editing" })
    .click();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toHaveCount(0);
  await page
    .getByRole("dialog", { name: "Settings", exact: true })
    .getByRole("button", { name: "Close", exact: true })
    .click();
  await expect(page.getByRole("dialog", { name: "Settings", exact: true })).toHaveCount(0);
  await expect(amount).toHaveValue("1,234");
  await page.context().setOffline(false);
  const event = page.waitForRequest(
    (request) => new URL(request.url()).pathname === "/api/operation.preview",
  );
  const identity = await reviewDraft(page);
  expect((await event).postDataJSON()).toMatchObject({
    operationId: identity.operationId,
    act: { values: { claimedAmount: "1234", claimedCurrency: "EUR" } },
  });
  const review = page.getByRole("dialog", { name: "Review changes", exact: true });
  await review.getByText("I confirm these changes.", { exact: true }).click();
  // Reviewed operations expose preferences inside the modal; the background header is inert.
  await review.getByRole("button", { name: "Reload interface", exact: true }).click();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Keep editing" })
    .click();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toHaveCount(0);
  await expect(review).toContainText(identity.operationId);
  await expect(review.getByRole("checkbox")).toBeChecked();
  expect(executions).toEqual([]);
});
