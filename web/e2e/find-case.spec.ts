import { randomUUID } from "node:crypto";
import { expect, test, type Request } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";

test("Find uses a modifier shortcut, debounces suggestions and routes exact and free text", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `FIND-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const apple = await page.evaluate(() => /Mac|iPhone|iPad|iPod/u.test(navigator.platform));
  await page.keyboard.press("k");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.keyboard.press(apple ? "Meta+k" : "Control+k");
  const input = page.getByRole("dialog").getByRole("combobox", { name: "Find a case" });
  await expect(input).toBeFocused();
  const queries: Readonly<Record<string, unknown>>[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname !== "/api/case.list") {
      return;
    }
    const payload = request.postDataJSON() as Readonly<Record<string, unknown>>;
    if (payload["limit"] === 8) {
      queries.push(payload);
    }
  });
  await input.fill(reference.slice(0, 3));
  await input.fill(reference);
  await expect.poll(() => queries).toEqual([{ view: "ALL", text: reference, limit: 8 }]);
  await input.press("Enter");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page).toHaveURL(/\/cases\/id\//u);
  await page.keyboard.press(apple ? "Meta+k" : "Control+k");
  await input.fill("Synthetic claimant");
  await input.press("Enter");
  await expect(page).toHaveURL(
    (url) =>
      url.pathname === "/cases" &&
      url.searchParams.get("view") === "ALL" &&
      url.searchParams.get("q") === "Synthetic claimant",
  );
});

test("editing Find discards a held exact case response @engine", async ({ page }) => {
  await openAuthenticated(page);
  const oldReference = `FIND-OLD-${randomUUID().slice(0, 8)}`;
  const currentReference = `FIND-CURRENT-${randomUUID().slice(0, 8)}`;
  await openCase(page, oldReference);
  const oldPath = new URL(page.url()).pathname;
  await page.goto("/cases");
  await openCase(page, currentReference);
  const currentPath = new URL(page.url()).pathname;
  await page.goto("/cases");

  const held = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const delivered = Promise.withResolvers<void>();
  const isOldLookup = (request: Request) =>
    new URL(request.url()).pathname === "/api/case.get" &&
    (request.postDataJSON() as { target?: { caseReference?: string } }).target?.caseReference ===
      oldReference;
  const oldCancelled = Promise.withResolvers<void>();
  page.on("requestfailed", (request) => {
    if (isOldLookup(request)) {
      oldCancelled.resolve();
    }
  });
  await page.route("**/api/case.get", async (route) => {
    if (!isOldLookup(route.request())) {
      await route.continue();
      return;
    }
    try {
      const response = await route.fetch();
      expect(response.ok()).toBe(true);
      held.resolve();
      await release.promise;
      await route.fulfill({ response }).catch(() => undefined);
    } catch (error) {
      held.reject(error);
      throw error;
    } finally {
      delivered.resolve();
    }
  });

  const dialog = page.getByRole("dialog", { name: "Find a case" });
  const input = dialog.getByRole("combobox");
  try {
    await page.locator(".shell-find:visible").click();
    await input.fill(oldReference);
    await input.press("Enter");
    await held.promise;
    await input.fill(currentReference);
    const suggestion = page.getByRole("option").filter({ hasText: currentReference });
    await expect(suggestion).toBeVisible();
    const oldNavigation = page.waitForURL((url) => url.pathname === oldPath);
    release.resolve();
    await delivered.promise;
    expect(
      await Promise.race([
        oldCancelled.promise.then(() => "cancelled"),
        oldNavigation.then(() => "opened the previous case"),
      ]),
    ).toBe("cancelled");
    await expect(dialog).toBeVisible();
    await expect(input).toHaveValue(currentReference);
    await suggestion.click();
    await expect(page).toHaveURL((url) => url.pathname === currentPath);
    await expect(dialog).toHaveCount(0);
  } finally {
    release.resolve();
  }
});
