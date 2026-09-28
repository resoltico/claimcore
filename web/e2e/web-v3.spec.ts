import { AxeBuilder } from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

import { login, logout } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

type SessionEnvelope = {
  endpoint: "session";
  outcome: { tag: "SNAPSHOT"; data: { authenticated: boolean; antiforgeryToken: string } };
};

type HostFailure = { kind: "HOST_FAILURE"; code: string; executionPhase: string | null };
type ApiReply<T> = { status: number; cacheControl: string | null; payload: T };

const readSession = async (page: import("@playwright/test").Page) =>
  page.evaluate(async () => {
    const response = await fetch("/api/v3/session", { credentials: "same-origin" });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as SessionEnvelope,
    } satisfies ApiReply<SessionEnvelope>;
  });

const readUnknownEndpoint = async (page: import("@playwright/test").Page) =>
  page.evaluate(async () => {
    const response = await fetch("/api/v3/not-present", { credentials: "same-origin" });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as HostFailure,
    } satisfies ApiReply<HostFailure>;
  });

const readProtectedList = async (page: import("@playwright/test").Page, token: string) =>
  page.evaluate(async (antiforgeryToken) => {
    const response = await fetch("/api/v3/cases/list", {
      method: "POST",
      credentials: "same-origin",
      headers: {
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
  }, token);

const expectAnonymousV2Boundary = async (page: import("@playwright/test").Page) => {
  const missing = await readUnknownEndpoint(page);
  expect(missing).toMatchObject({
    status: 404,
    cacheControl: expect.stringContaining("no-store"),
    payload: { kind: "HOST_FAILURE", code: "WEB_NOT_FOUND" },
  });
  const session = await readSession(page);
  expect(session).toMatchObject({
    status: 200,
    cacheControl: expect.stringContaining("no-store"),
    payload: { endpoint: "session", outcome: { tag: "SNAPSHOT", data: { authenticated: false } } },
  });
  return session.payload.outcome.data.antiforgeryToken;
};

const expectSessionRejection = async (page: import("@playwright/test").Page, token: string) => {
  const rejected = await readProtectedList(page, token);
  expect(rejected).toMatchObject({
    status: 401,
    cacheControl: expect.stringContaining("no-store"),
    payload: { kind: "HOST_FAILURE", code: "WEB_SESSION_REJECTED", executionPhase: null },
  });
};

const expectRetiredBootstrapRoute = async (page: import("@playwright/test").Page) => {
  const response = await page.request.post("/api/v3/session/login", { data: {} });
  expect(response.status()).toBe(404);
  expect(response.headers()["cache-control"]).toContain("no-store");
};

const expectAnonymousSession = async (page: import("@playwright/test").Page) => {
  const session = await readSession(page);
  expect(session).toMatchObject({
    status: 200,
    payload: { endpoint: "session", outcome: { tag: "SNAPSHOT", data: { authenticated: false } } },
  });
};

test("uses OIDC sign-in and the accessible actor-bound case workspace", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  const antiforgeryToken = await expectAnonymousV2Boundary(page);
  await expectSessionRejection(page, antiforgeryToken);
  await expectRetiredBootstrapRoute(page);
  await login(page);
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expect(readSession(page)).resolves.toMatchObject({
    status: 200,
    payload: { endpoint: "session", outcome: { tag: "SNAPSHOT", data: { authenticated: true } } },
  });
  await expect(new AxeBuilder({ page }).analyze()).resolves.toMatchObject({ violations: [] });
  await logout(page);
  await expectAnonymousSession(page);
});
