import { expect, test, type APIResponse, type Page } from "@playwright/test";

import { expectAccessible, expectHostFailure, login, progress } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });
const cleanup: { required: boolean; batch: Promise<PromiseSettledResult<APIResponse>[]> | null } = {
  required: false,
  batch: null,
};
const resetWindow = async (page: Page) => {
  if (cleanup.required) {
    if (cleanup.batch !== null) {
      await cleanup.batch;
    }
    await progress("rate-window-wait");
    await page.waitForTimeout(61_000);
    cleanup.required = false;
    cleanup.batch = null;
  }
};
test.afterEach(async ({ page }) => {
  await resetWindow(page);
});

test("bounds repeated published login admission and recovers after its window", async ({
  page,
}) => {
  test.setTimeout(80_000);
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  await progress("rate-probing");
  let bounded = false;
  // The shared host may already have consumed permits. A burst crosses at most one
  // window boundary; eleven requests exceed both possible five-permit allocations.
  cleanup.required = true;
  const began = performance.now();
  cleanup.batch = Promise.allSettled(
    Array.from({ length: 11 }, () => page.request.get("/auth/login", { maxRedirects: 0 })),
  );
  const responses = await cleanup.batch;
  expect(performance.now() - began).toBeLessThan(60_000);
  for (const result of responses) {
    if (result.status !== "fulfilled") {
      throw new Error("E2E_RATE_REQUEST_FAILED");
    }
    const response = result.value;
    if (response.status() === 429) {
      await expectHostFailure(
        {
          status: 429,
          cacheControl: response.headers()["cache-control"] ?? null,
          payload: await response.json(),
        },
        429,
        "WEB_BUSY",
      );
      bounded = true;
      continue;
    }
    if (response.status() !== 302) {
      throw new Error(`E2E_RATE_STATUS_${response.status()}`);
    }
  }
  expect(bounded).toBe(true);
  await resetWindow(page);
  await progress("rate-window-ended");
  await page.reload({ waitUntil: "commit" });
  await login(page);
  await progress("rate-login-ready");
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expectAccessible(page);
});
