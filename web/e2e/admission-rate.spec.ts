import { expect, test } from "@playwright/test";

import {
  expectAccessible,
  expectHostFailure,
  login,
  progress,
  rejectedLogin,
  sessionToken,
} from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

test("bounds repeated published login admission and recovers after its window", async ({
  page,
}) => {
  test.setTimeout(80_000);
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeEnabled();
  const token = await sessionToken(page);
  await progress("rate-probing");
  let refused = 0;
  let bounded = false;
  // Distinct deliberate bad logins exercise admission; no request is retried.
  for (let attempt = 0; attempt < 6; attempt += 1) {
    const reply = await rejectedLogin(page, token);
    if (reply.status === 429) {
      await expectHostFailure(reply, 429, "WEB_BUSY");
      bounded = true;
      break;
    }
    if (reply.status !== 401) throw new Error(`E2E_RATE_STATUS_${reply.status}`);
    await expectHostFailure(reply, 401, "WEB_LOGIN_REJECTED");
    refused += 1;
  }
  expect(refused).toBeGreaterThanOrEqual(2);
  expect(bounded).toBe(true);
  await progress("rate-window-wait");
  await page.waitForTimeout(61_000);
  await progress("rate-window-ended");
  await page.reload();
  await login(page);
  await progress("rate-login-ready");
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expectAccessible(page);
});
