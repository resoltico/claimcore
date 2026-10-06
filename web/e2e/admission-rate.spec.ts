import { expect, test } from "@playwright/test";

import { expectAccessible, expectHostFailure, login, progress } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

test("bounds repeated published login admission and recovers after its window", async ({
  page,
}) => {
  test.setTimeout(80_000);
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  await progress("rate-probing");
  let challenges = 0;
  let bounded = false;
  // Challenge requests exercise admission without submitting any credentials.
  for (let attempt = 0; attempt < 6; attempt += 1) {
    const response = await page.request.get("/auth/login", { maxRedirects: 0 });
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
      break;
    }
    if (response.status() !== 302) {
      throw new Error(`E2E_RATE_STATUS_${response.status()}`);
    }
    challenges += 1;
  }
  expect(challenges).toBeGreaterThanOrEqual(2);
  expect(bounded).toBe(true);
  await progress("rate-window-wait");
  await page.waitForTimeout(61_000);
  await progress("rate-window-ended");
  await page.reload({ waitUntil: "commit" });
  await login(page);
  await progress("rate-login-ready");
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expectAccessible(page);
});
