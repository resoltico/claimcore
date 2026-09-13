import { readFile } from "node:fs/promises";
import { AxeBuilder } from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

import { logout, rejectedLogin } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

type SessionEnvelope = {
  endpoint: "session";
  outcome: { tag: "SNAPSHOT"; data: { authenticated: boolean; antiforgeryToken: string } };
};

type HostFailure = { kind: "HOST_FAILURE"; code: string; executionPhase: string | null };
type ApiReply<T> = { status: number; cacheControl: string | null; payload: T };

const credentialFile = process.env["CLAIMCORE_WEB_BOOTSTRAP_CREDENTIAL_FILE"];
if (credentialFile === undefined) throw new Error("Browser credential file was not configured.");

const readSession = async (page: import("@playwright/test").Page) =>
  page.evaluate(async () => {
    const response = await fetch("/api/v2/session", { credentials: "same-origin" });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as SessionEnvelope,
    } satisfies ApiReply<SessionEnvelope>;
  });

const readUnknownEndpoint = async (page: import("@playwright/test").Page) =>
  page.evaluate(async () => {
    const response = await fetch("/api/v2/not-present", { credentials: "same-origin" });
    return {
      status: response.status,
      cacheControl: response.headers.get("cache-control"),
      payload: (await response.json()) as HostFailure,
    } satisfies ApiReply<HostFailure>;
  });

const readProtectedList = async (page: import("@playwright/test").Page, token: string) =>
  page.evaluate(async (antiforgeryToken) => {
    const response = await fetch("/api/v2/cases/list", {
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

const expectLoginRejection = async (page: import("@playwright/test").Page, token: string) => {
  const rejected = await rejectedLogin(page, token);
  expect(rejected).toMatchObject({
    status: 401,
    cacheControl: expect.stringContaining("no-store"),
    payload: { kind: "HOST_FAILURE", code: "WEB_LOGIN_REJECTED", executionPhase: "NOT_STARTED" },
  });
};

const expectAnonymousSession = async (page: import("@playwright/test").Page) => {
  const session = await readSession(page);
  expect(session).toMatchObject({
    status: 200,
    payload: { endpoint: "session", outcome: { tag: "SNAPSHOT", data: { authenticated: false } } },
  });
};

test("uses the published v2 login boundary and accessible case workspace", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeEnabled();
  const antiforgeryToken = await expectAnonymousV2Boundary(page);
  await expectSessionRejection(page, antiforgeryToken);
  await expectLoginRejection(page, antiforgeryToken);
  const credential = (await readFile(credentialFile, "utf8")).trim();
  await page.getByLabel("Bootstrap credential").fill(credential);
  const loginResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v2/session/login",
  );
  await page.getByRole("button", { name: "Sign in" }).click();
  const loginStatus = (await loginResponse).status();
  if (loginStatus !== 200) throw new Error(`E2E_BOUNDARY_LOGIN_STATUS_${loginStatus}`);
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
  await expect(readSession(page)).resolves.toMatchObject({
    status: 200,
    payload: { endpoint: "session", outcome: { tag: "SNAPSHOT", data: { authenticated: true } } },
  });
  await expect(new AxeBuilder({ page }).analyze()).resolves.toMatchObject({ violations: [] });
  await logout(page);
  await expectAnonymousSession(page);
});
