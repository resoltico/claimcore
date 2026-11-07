import { goToView } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { startOpen, reviewDraft } from "./case-workflow";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage, settingsNavigation } from "./localization-support";

const accessModule = /\/assets\/workspace~AuthorityPanel-[^/]+\.js$/u;

const reloadDocument = async (page: Page) => {
  const before = await page.evaluate(() => window.performance.timeOrigin);
  await Promise.all([
    page.waitForEvent("framenavigated", { predicate: (frame) => frame === page.mainFrame() }),
    page.getByRole("button", { name: "Reload interface", exact: true }).click(),
  ]);
  await page.waitForLoadState("domcontentloaded");
  await expect.poll(() => page.evaluate(() => window.performance.timeOrigin)).not.toBe(before);
};

test("public bytes and private responses have separate cache policies", async ({ page }) => {
  const assets: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/assets/")) {
      assets.push(path);
    }
  });
  await openAuthenticated(page);
  await page.waitForLoadState("networkidle");
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

for (const language of ["lv", "zh-Hant"] as const) {
  test(`a missing ${language} chunk leaves the working pair and direction intact and supports explicit recovery`, async ({
    page,
  }) => {
    const languageModule = new RegExp(`/assets/language-${language}-[^/]+\\.js$`, "u");
    await openAuthenticated(page);
    await page.route(languageModule, (route) =>
      route.fulfill({
        status: 404,
        contentType: "text/plain",
        headers: { "Cache-Control": "no-store" },
        body: "SYNTHETIC_MISSING_LANGUAGE_CHUNK",
      }),
    );
    await (await settingsNavigation(page)).click();
    const selected = page.locator(`.settings-panel input[name$="-language"][value="${language}"]`);
    const english = page.locator('.settings-panel input[name$="-language"][value="en"]');
    // Request the failing activation; check() would require a selected state that must roll back.
    await selected.click();
    await expect(page.getByRole("alert")).toContainText("The language could not be loaded");
    await expect(selected).not.toBeChecked();
    await expect(english).toBeChecked();
    await expect(page.getByRole("button", { name: "Retry language", exact: true })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await page.getByRole("button", { name: "Retry language", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText("The language could not be loaded");
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await page.unroute(languageModule);
    // Browsers may retain a failed module fetch in the document's module map.
    // An explicit browser reload clears that map and retains the last admitted pair.
    const before = await page.evaluate(() => window.performance.timeOrigin);
    await page.reload({ waitUntil: "domcontentloaded" });
    await expect.poll(() => page.evaluate(() => window.performance.timeOrigin)).not.toBe(before);
    await expect(page.getByRole("heading", { name: "Settings", exact: true })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await selectLanguage(page, language);
    await expect(page.locator("html")).toHaveAttribute("lang", language);
  });
}

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
  await goToView(page, "people");
  await expect(page.getByRole("alert")).toContainText("This view could not be loaded");
  await expect(page.locator('.workspace-sidebar nav a[href="/reminders"]')).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry loading", exact: true })).toBeVisible();
  await page.unroute(accessModule);
  await reloadDocument(page);
  await expect(page.getByRole("heading", { name: "People & access", exact: true })).toBeVisible();
  await expect(page.locator(".authority-panel")).toBeVisible();
  const addPerson = page
    .getByRole("button", { name: "Add person", exact: true })
    .and(page.locator(".authority-panel > button"));
  await expect(addPerson).toHaveCount(1);
  await addPerson.click();
  await expect(page.locator('[name="target.subject"]')).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test("offline Settings navigation guard preserves raw entry, review identity and confirmation", async ({
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
  const editorUrl = page.url();
  await (await settingsNavigation(page)).click();
  await expect(page).toHaveURL((url) => url.pathname === "/settings");
  await expect(page.locator(".settings-panel")).toBeVisible();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toHaveCount(0);
  await page.locator("[data-return-workspace]").click();
  await expect(page).toHaveURL(editorUrl);
  await expect(page.locator(".settings-panel")).toHaveCount(0);
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
  const review = page
    .getByRole("dialog")
    .filter({ has: page.locator("button[data-confirm-operation]") });
  await review.locator("button[data-confirm-operation]").focus();
  await expect(review.locator('.settings-panel, [role="combobox"][id$="-format"]')).toHaveCount(0);
  await expect(review).toContainText(identity.operationId);
  await expect(review.getByRole("checkbox")).toHaveCount(0);
  await expect(review.locator("button[data-confirm-operation]")).toBeEnabled();
  expect(executions).toEqual([]);
});
