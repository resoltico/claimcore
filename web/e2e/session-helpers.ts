import { readFile, writeFile } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";

import { AxeBuilder } from "@axe-core/playwright";
import { expect, type BrowserContext, type Page } from "@playwright/test";

import { isHostFailure, isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { HostFailure, WebV3Response } from "../src/generated/contracts/web-v3.types";

type BrowserReply = Readonly<{
  status: number;
  cacheControl: string | null;
  payload: unknown;
}>;

export const progress = async (stage: string): Promise<void> => {
  const file = process.env["CLAIMCORE_WEB_E2E_PROGRESS_FILE"];
  if (file !== undefined) {
    await writeFile(file, stage);
  }
};

const oidcCredentialsFile = process.env["CLAIMCORE_TEST_OIDC_CREDENTIALS"];
if (oidcCredentialsFile === undefined) {
  throw new Error("Synthetic OIDC credentials were not configured.");
}

const syntheticOwner = async (): Promise<{ username: string; password: string }> => {
  const source: unknown = JSON.parse(await readFile(oidcCredentialsFile, "utf8"));
  if (typeof source !== "object" || source === null || !("users" in source)) {
    throw new Error("Synthetic OIDC user inventory is invalid.");
  }
  const { users } = source;
  if (!Array.isArray(users)) {
    throw new Error("Synthetic OIDC user inventory is invalid.");
  }
  const owner: unknown = users[0];
  if (
    typeof owner !== "object" ||
    owner === null ||
    !("username" in owner) ||
    typeof owner.username !== "string" ||
    !("password" in owner) ||
    typeof owner.password !== "string"
  ) {
    throw new Error("Synthetic OIDC owner is invalid.");
  }
  return { username: owner.username, password: owner.password };
};
type Cookies = Awaited<ReturnType<BrowserContext["cookies"]>>;

export const openAuthenticated = async (
  page: Page,
  casesHeading: string | RegExp = "Cases",
): Promise<void> => {
  const output = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (output === undefined) {
    throw new Error("Private browser output was not configured.");
  }
  const state: unknown = JSON.parse(
    await readFile(resolve(output, "authenticated-state.json"), "utf8"),
  );
  if (
    typeof state !== "object" ||
    state === null ||
    !("cookies" in state) ||
    !Array.isArray(state.cookies)
  ) {
    throw new Error("E2E_AUTH_STATE_INVALID");
  }
  await page.context().addCookies(state.cookies as Cookies);
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: casesHeading, exact: true })).toBeVisible();
};

export const expectAccessible = async (page: Page): Promise<void> => {
  const result = await new AxeBuilder({ page }).analyze();
  if (result.violations.length === 0) {
    return;
  }
  const ruleIds = result.violations
    .map((violation) => violation.id.replace(/[^a-zA-Z0-9]+/gu, "_").toUpperCase())
    .sort()
    .join("__");
  throw new Error(`E2E_AXE_${ruleIds}`);
};

export const browserRequest = (
  page: Page,
  path: string,
  init?: Readonly<{
    method?: string;
    headers?: Readonly<Record<string, string>>;
    body?: string;
  }>,
): Promise<BrowserReply> =>
  page.evaluate(
    async ({ requestPath, requestInit }) => {
      const response = await fetch(requestPath, {
        ...requestInit,
        credentials: "same-origin",
      });
      const payload: unknown = await response.json();
      return {
        status: response.status,
        cacheControl: response.headers.get("cache-control"),
        payload,
      };
    },
    { requestPath: path, requestInit: init },
  );

export const sessionToken = async (page: Page): Promise<string> => {
  const reply = await browserRequest(page, "/api/v3/session");
  if (!(await isWebV3Response("session", reply.payload))) {
    throw new Error("Invalid session response.");
  }
  const snapshot = (reply.payload as WebV3Response<"session">).outcome.data;
  if (reply.status !== 200 || snapshot.antiforgeryToken === null) {
    throw new Error("Session response did not provide an antiforgery token.");
  }
  return snapshot.antiforgeryToken;
};

export const expectHostFailure = async (
  reply: BrowserReply,
  status: number,
  code: string,
): Promise<void> => {
  expect(reply.status).toBe(status);
  expect(reply.cacheControl).toContain("no-store");
  const valid = await isHostFailure(reply.payload, reply.status);
  expect(valid).toBe(true);
  if (!valid) {
    throw new Error("Invalid host-failure response.");
  }
  expect((reply.payload as HostFailure).code).toBe(code);
};

const awaitOidcReturn = async (
  page: Page,
  applicationOrigin: string,
  callbackStatus: () => number,
): Promise<void> => {
  try {
    await page.waitForURL((url) => url.origin === applicationOrigin && url.pathname === "/", {
      timeout: 10_000,
    });
  } catch {
    const current = new URL(page.url());
    const safeFile = process.env["CLAIMCORE_WEB_E2E_SAFE_FAILURE_FILE"];
    if (safeFile !== undefined) {
      if (!isAbsolute(safeFile)) {
        throw new Error("Safe failure path must be absolute.");
      }
      await writeFile(
        safeFile,
        `${JSON.stringify({ callbackStatus: callbackStatus(), origin: current.origin, pathname: current.pathname })}\n`,
        { mode: 0o600 },
      );
    }
    const originClass = current.origin === applicationOrigin ? "app" : "issuer";
    const pathClass = current.pathname.replace(/[^a-z0-9]+/giu, "-").slice(0, 80);
    await progress(`oidc-return-failed-${callbackStatus()}-${originClass}-${pathClass}`);
    throw new Error(`E2E_OIDC_RETURN_${callbackStatus()}_${current.origin}${current.pathname}`);
  }
};

export const login = async (page: Page): Promise<void> => {
  await progress("login-start");
  const cases = page.getByRole("heading", { name: "Cases" });
  try {
    await page
      .getByRole("heading", { name: /^(Cases|ClaimCore)$/u })
      .first()
      .waitFor({ timeout: 5_000 });
  } catch {
    const main = await page.evaluate(() => document.querySelector("main")?.className ?? "none");
    const kind = ["loading", "login-shell", "app-shell"].includes(main) ? main : "other";
    throw new Error(`E2E_LOGIN_VIEW_${kind.toUpperCase().replace(/-/gu, "_")}`);
  }
  if (await cases.isVisible()) {
    await progress("login-auth");
    await progress("login-ready");
    return;
  }
  await progress("login-anon");
  const applicationOrigin = new URL(page.url()).origin;
  const owner = await syntheticOwner();
  await page.getByRole("link", { name: "Sign in" }).click();
  await progress("oidc-navigation");
  await page.locator('input[name="username"]').fill(owner.username);
  await progress("oidc-username-filled");
  await page.locator('input[name="password"]').fill(owner.password);
  await progress("oidc-password-filled");
  let callbackStatus = 0;
  page.on("response", (response) => {
    if (new URL(response.url()).pathname === "/signin-oidc") {
      callbackStatus = response.status();
    }
  });
  await page.locator('input[type="submit"], button[type="submit"]').first().click();
  await progress("oidc-submitted");
  await awaitOidcReturn(page, applicationOrigin, () => callbackStatus);
  await progress("oidc-callback-returned");
  await expect(cases).toBeVisible();
  await progress("login-ready");
};

export const logout = async (page: Page): Promise<void> => {
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v3/session/logout",
  );
  await page.getByRole("button", { name: "Sign out" }).click();
  const response = await responseEvent;
  expect(response.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  const reply = await browserRequest(page, "/api/v3/session");
  if (!(await isWebV3Response("session", reply.payload))) {
    throw new Error("Invalid logout response.");
  }
  expect((reply.payload as WebV3Response<"session">).outcome.data.authenticated).toBe(false);
};
