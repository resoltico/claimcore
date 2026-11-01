import { randomUUID } from "node:crypto";
import { startOpen, reviewDraft } from "./case-workflow";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage } from "./localization-support";

const accessModule = /\/assets\/workspace~AuthorityPanel-[^/]+\.js$/u;
const latvianModule = /\/assets\/language-lv-[^/]+\.js$/u;

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
  await page.route(latvianModule, (route) => route.abort("failed"));
  await page.locator("[data-settings-button]").click();
  await page.getByRole("combobox", { name: "Interface language" }).selectOption("lv");
  await expect(page.getByRole("alert")).toContainText("The language could not be loaded");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  await page.unroute(latvianModule);
  // Browsers may retain a failed module fetch in the document's module map.
  // Explicit reload is available and keeps persisted preferences at the last admitted pair.
  await page.getByRole("button", { name: "Reload interface", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await selectLanguage(page, "lv");
  await expect(page.locator("html")).toHaveAttribute("lang", "lv");
});

test("a missing view chunk leaves the workspace available and explicit reload installs the current view", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.route(accessModule, (route) => route.abort("failed"));
  await page.locator('.workspace-header nav a[href="/authority"]').click();
  await expect(page.getByRole("alert")).toContainText("This view could not be loaded");
  await expect(page.locator('.workspace-header nav a[href="/operations"]')).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry loading", exact: true })).toBeVisible();
  await page.unroute(accessModule);
  await page.getByRole("button", { name: "Reload interface", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Access", exact: true })).toBeVisible();
});

test("failed language activation and a declined update reload preserve raw entry, review identity and confirmation", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `CHUNK-DRAFT-${randomUUID()}`);
  const amount = page.locator('input[name="act.values.claimed.amount"]');
  await amount.fill("1,234");
  const executions: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions.push(request.postData() ?? "");
    }
  });
  await page.route(latvianModule, (route) => route.abort("failed"));
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
  await page.keyboard.press("Escape");
  await expect(amount).toHaveValue("1,234");
  const event = page.waitForRequest(
    (request) => new URL(request.url()).pathname === "/api/operation.preview",
  );
  const identity = await reviewDraft(page);
  expect((await event).postDataJSON()).toMatchObject({
    operationId: identity.operationId,
    act: { values: { claimed: { amount: "1234", currency: "EUR" } } },
  });
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.locator("[data-settings-button]").click();
  await page.getByRole("button", { name: "Reload interface", exact: true }).click();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Keep editing" })
    .click();
  await page.keyboard.press("Escape");
  const review = page.getByRole("dialog", { name: "Review changes", exact: true });
  await expect(review).toContainText(identity.operationId);
  await expect(review.getByRole("checkbox")).toBeChecked();
  expect(executions).toEqual([]);
});
