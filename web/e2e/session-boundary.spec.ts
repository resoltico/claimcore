import { AxeBuilder } from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

import { consumerContractIdentity } from "@contracts/client";
import { login, logout } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

type SessionEnvelope = {
  outcome: { tag: "SUCCEEDED"; data: { authenticated: boolean; antiforgeryToken: string } };
};

type HostFailure = { hostFailure: { code: string; phase: string | null } };
type ApiReply<T> = { status: number; cacheControl: string | null; payload: T };

const readSession = (page: Page) =>
  page.evaluate(async (fingerprint) => {
    const response = await fetch("/api/session", {
      credentials: "same-origin",
      headers: { "ClaimCore-Contract": fingerprint },
    });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as SessionEnvelope,
    } satisfies ApiReply<SessionEnvelope>;
  }, consumerContractIdentity);

const readUnknownEndpoint = (page: Page) =>
  page.evaluate(async () => {
    const response = await fetch("/api/service/not-present", { credentials: "same-origin" });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as HostFailure,
    } satisfies ApiReply<HostFailure>;
  });

const readProtectedList = (page: Page, token: string) =>
  page.evaluate(
    async ({ antiforgeryToken, fingerprint }) => {
      const response = await fetch("/api/case.list", {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "ClaimCore-Contract": fingerprint,
          "Content-Type": "application/json",
          "X-ClaimCore-Antiforgery": antiforgeryToken,
        },
        body: JSON.stringify({ limit: 1 }),
      });
      return {
        status: response.status,
        cacheControl: response.headers.get("cache-control"),
        payload: (await response.json()) as HostFailure,
      } satisfies ApiReply<HostFailure>;
    },
    { antiforgeryToken: token, fingerprint: consumerContractIdentity },
  );

const expectAnonymousSessionBoundary = async (page: Page) => {
  const missing = await readUnknownEndpoint(page);
  expect(missing).toMatchObject({
    status: 404,
    cacheControl: expect.stringContaining("no-store"),
    payload: { hostFailure: { code: "ENDPOINT_MISSING" } },
  });
  const session = await readSession(page);
  expect(session).toMatchObject({
    status: 200,
    cacheControl: expect.stringContaining("no-store"),
    payload: { outcome: { tag: "SUCCEEDED", data: { authenticated: false } } },
  });
  return session.payload.outcome.data.antiforgeryToken;
};

const expectSessionRejection = async (page: Page, token: string) => {
  const rejected = await readProtectedList(page, token);
  expect(rejected).toMatchObject({
    status: 401,
    cacheControl: expect.stringContaining("no-store"),
    payload: { hostFailure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" } },
  });
};

const expectAbsentRoute = async (page: Page) => {
  const response = await page.request.post("/api/session/login", { data: {} });
  expect(response.status()).toBe(404);
  expect(response.headers()["cache-control"]).toContain("no-store");
};

const expectAnonymousSession = async (page: Page) => {
  const session = await readSession(page);
  expect(session).toMatchObject({
    status: 200,
    payload: { outcome: { tag: "SUCCEEDED", data: { authenticated: false } } },
  });
};

test("uses OIDC sign-in and the accessible actor-bound case workspace", async ({ page }) => {
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  const antiforgeryToken = await expectAnonymousSessionBoundary(page);
  await expectSessionRejection(page, antiforgeryToken);
  await expectAbsentRoute(page);
  await login(page);
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expect(readSession(page)).resolves.toMatchObject({
    status: 200,
    payload: { outcome: { tag: "SUCCEEDED", data: { authenticated: true } } },
  });
  await expect(new AxeBuilder({ page }).analyze()).resolves.toMatchObject({ violations: [] });
  await logout(page);
  await expectAnonymousSession(page);
});
