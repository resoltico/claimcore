import { expect, test, type Page } from "@playwright/test";

import { freshLoginWorkflowTimeout } from "./admission-window";
import { expectAccessible, expectHostFailure, login, progress } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const exhaustLoginWindow = async (page: Page) => {
  let bounded = false;
  // The shared host may already have consumed permits. A burst crosses at most one
  // window boundary; eleven requests exceed both possible five-permit allocations.
  const began = performance.now();
  const responses = await Promise.allSettled(
    Array.from({ length: 11 }, () => page.request.get("/auth/login", { maxRedirects: 0 })),
  );
  expect(performance.now() - began).toBeLessThan(60_000);
  for (const result of responses) {
    if (result.status !== "fulfilled") {
      throw new Error("E2E_RATE_REQUEST_FAILED");
    }
    const response = result.value;
    if (response.status() === 429) {
      expectHostFailure(
        {
          status: 429,
          cacheControl: response.headers()["cache-control"] ?? null,
          payload: await response.json(),
        },
        429,
        "RATE_LIMITED",
      );
      bounded = true;
      continue;
    }
    if (response.status() !== 302) {
      throw new Error(`E2E_RATE_STATUS_${response.status()}`);
    }
  }
  expect(bounded).toBe(true);
};

test("bounds repeated published login admission and recovers after its window", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  await progress("rate-probing");
  await exhaustLoginWindow(page);
  await progress("rate-window-recovery-start");
  const challenges: number[] = [];
  page.on("response", (response) => {
    if (
      new URL(response.url()).pathname === "/auth/login" &&
      response.request().isNavigationRequest()
    ) {
      challenges.push(response.status());
    }
  });
  await login(page);
  expect(challenges.at(-1)).toBe(302);
  expect(challenges.filter((status) => status === 302)).toHaveLength(1);
  test.info().annotations.push({
    type: "browser-rate-refusals",
    description: String(challenges.filter((status) => status === 429).length),
  });
  await progress("rate-login-ready");
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expectAccessible(page);
});
