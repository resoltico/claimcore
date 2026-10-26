import {
  loginNavigationTimeout,
  loginAdmissionAttempt,
  waitForLoginPermit,
} from "./admission-window";
import { readFile, writeFile } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";

import { AxeBuilder } from "@axe-core/playwright";
import { expect, type BrowserContext, type Page } from "@playwright/test";

import { consumerContractIdentity } from "@contracts/client";
import { isHostFailure, isResponse } from "../src/protocol/client";
import { retainResponseBodies } from "./response-bodies";
import { observeOidcSubmission, providerFailure } from "./oidc-provider-evidence.mjs";

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

const credentialsPath = (): string => {
  const path = process.env["CLAIMCORE_TEST_OIDC_CREDENTIALS"];
  if (path === undefined) {
    throw new Error("Synthetic OIDC credentials were not configured.");
  }
  return path;
};

const syntheticCredentials = async (
  username?: string,
): Promise<{ username: string; password: string }> => {
  const source: unknown = JSON.parse(await readFile(credentialsPath(), "utf8"));
  if (typeof source !== "object" || source === null || !("users" in source)) {
    throw new Error("Synthetic OIDC user inventory is invalid.");
  }
  const { users } = source;
  if (!Array.isArray(users)) {
    throw new Error("Synthetic OIDC user inventory is invalid.");
  }
  const owner: unknown =
    username === undefined
      ? users[0]
      : users.find(
          (user: unknown) =>
            typeof user === "object" &&
            user !== null &&
            "username" in user &&
            user.username === username,
        );
  if (
    typeof owner !== "object" ||
    owner === null ||
    !("username" in owner) ||
    typeof owner.username !== "string" ||
    !("password" in owner) ||
    typeof owner.password !== "string"
  ) {
    throw new Error("Synthetic OIDC selected user is invalid.");
  }
  return { username: owner.username, password: owner.password };
};
type Cookies = Awaited<ReturnType<BrowserContext["cookies"]>>;

export const openApplication = async (page: Page, heading: string | RegExp): Promise<void> => {
  await retainResponseBodies(page);
  await page.goto("/", { waitUntil: "commit" });
  // Readiness uses the heading of the actor's advertised workspace.
  await expect(page.getByRole("heading", { name: heading, exact: true })).toBeVisible({
    timeout: 10_000,
  });
};

export const openAuthenticated = async (
  page: Page,
  casesHeading: string | RegExp = "Cases",
  stateFile:
    "authenticated-state.json" | "narrow-authenticated-state.json" = "authenticated-state.json",
): Promise<void> => {
  const output = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (output === undefined) {
    throw new Error("Private browser output was not configured.");
  }
  const state: unknown = JSON.parse(await readFile(resolve(output, stateFile), "utf8"));
  if (
    typeof state !== "object" ||
    state === null ||
    !("cookies" in state) ||
    !Array.isArray(state.cookies)
  ) {
    throw new Error("E2E_AUTH_STATE_INVALID");
  }
  await page.context().addCookies(state.cookies as Cookies);
  await openApplication(page, casesHeading);
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
    async ({ requestPath, requestInit, fingerprint }) => {
      const response = await fetch(requestPath, {
        ...requestInit,
        headers: { "ClaimCore-Contract": fingerprint, ...requestInit?.headers },
        credentials: "same-origin",
      });
      const payload: unknown = await response.json();
      return {
        status: response.status,
        cacheControl: response.headers.get("cache-control"),
        payload,
      };
    },
    { requestPath: path, requestInit: init, fingerprint: consumerContractIdentity },
  );

export const sessionToken = async (page: Page): Promise<string> => {
  const reply = await browserRequest(page, "/api/session");
  if (!isResponse("session", reply.payload)) {
    throw new Error("Invalid session response.");
  }
  if (reply.payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("Session refused.");
  }
  const snapshot = reply.payload.outcome.data;
  if (reply.status !== 200 || snapshot.antiforgeryToken === null) {
    throw new Error("Session response did not provide an antiforgery token.");
  }
  return snapshot.antiforgeryToken;
};

export const expectHostFailure = (reply: BrowserReply, status: number, code: string): void => {
  expect(reply.status).toBe(status);
  expect(reply.cacheControl).toContain("no-store");
  const valid = isHostFailure(reply.payload, reply.status);
  expect(valid).toBe(true);
  if (!valid) {
    throw new Error("Invalid host-failure response.");
  }
  expect(reply.payload.hostFailure.code).toBe(code);
};

const awaitOidcReturn = async (
  page: Page,
  applicationOrigin: string,
  callbackStatus: () => number,
  submitted: ReturnType<typeof observeOidcSubmission>,
  issuerOrigin: string,
): Promise<void> => {
  try {
    await page.waitForURL((url) => url.origin === applicationOrigin && url.pathname === "/", {
      timeout: 10_000,
      waitUntil: "commit",
    });
  } catch {
    const current = new URL(page.url());
    let location = "other";
    if (current.origin === applicationOrigin) {
      location = "app";
    } else if (current.origin === issuerOrigin) {
      location = "issuer";
    }
    const snapshot = { callbackStatus: callbackStatus(), location, submission: submitted() };
    const provider = location === "issuer" ? await providerFailure(page) : "none";
    const evidence = { ...snapshot, provider };
    const safeFile = process.env["CLAIMCORE_WEB_E2E_SAFE_FAILURE_FILE"];
    if (safeFile !== undefined) {
      if (!isAbsolute(safeFile)) {
        throw new Error("Safe failure path must be absolute.");
      }
      await writeFile(safeFile, `${JSON.stringify(evidence)}\n`, { mode: 0o600 });
    }
    const category = `${snapshot.callbackStatus}_${location}_${provider}_${snapshot.submission.status}_${snapshot.submission.credentialMatch}`;
    await progress(`oidc-return-failed-${category}`);
    throw new Error(`E2E_OIDC_RETURN_${category}`);
  }
};

const trackOidcCallback = (page: Page, applicationOrigin: string) => {
  let callbackStatus = 0;
  page.on("response", (response) => {
    const url = new URL(response.url());
    if (url.origin === applicationOrigin && url.pathname === "/signin-oidc") {
      callbackStatus = response.status();
    }
  });
  return () => callbackStatus;
};

const fillOidcCredentials = async (
  page: Page,
  owner: Readonly<{ username: string; password: string }>,
): Promise<void> => {
  await page.locator('input[name="username"]').fill(owner.username);
  await progress("oidc-username-filled");
  await page.locator('input[name="password"]').fill(owner.password);
  await progress("oidc-password-filled");
};

const loginAttempt = (page: Page, address: URL, signIn: string | null, remaining: number) => {
  const controller = new AbortController();
  const response = page.waitForResponse(
    (value) =>
      value.url() === address.href &&
      value.request().isNavigationRequest() &&
      value.request().frame() === page.mainFrame(),
    { timeout: remaining },
  );
  const options = { timeout: loginNavigationTimeout, signal: controller.signal };
  const navigation =
    signIn === null
      ? page.goto(address.href, { ...options, waitUntil: "domcontentloaded" })
      : page.getByRole("link", { name: signIn, exact: true }).click(options);
  return loginAdmissionAttempt(response, navigation, controller, remaining);
};
const awaitLoginAdmission = async (page: Page, signIn: string, applicationOrigin: string) => {
  const href = await page.getByRole("link", { name: signIn, exact: true }).getAttribute("href");
  if (href === null) {
    throw new Error("E2E_LOGIN_LINK_MISSING");
  }
  const address = new URL(href, applicationOrigin);
  if (
    address.origin !== applicationOrigin ||
    address.pathname !== "/auth/login" ||
    address.search !== "" ||
    address.hash !== ""
  ) {
    throw new Error("E2E_LOGIN_LINK_INVALID");
  }
  let first = true;
  let acceptedNavigation: Promise<unknown> = Promise.resolve();
  await waitForLoginPermit(
    async (remaining) => {
      const attempt = await loginAttempt(page, address, first ? signIn : null, remaining);
      first = false;
      if (attempt.status === 302) {
        acceptedNavigation = attempt.navigation;
      }
      return attempt.status;
    },
    (milliseconds) => page.waitForTimeout(milliseconds),
  );
  await acceptedNavigation;
};
export const login = async (
  page: Page,
  workspaceHeading: string | null = "Cases",
  signIn = "Sign in",
  username?: string,
): Promise<void> => {
  await progress("login-start");
  await retainResponseBodies(page);
  const authenticated =
    workspaceHeading === null
      ? page.getByRole("button", { name: "Sign out", exact: true })
      : page.getByRole("heading", { name: workspaceHeading, exact: true });
  try {
    await authenticated
      .or(page.getByRole("heading", { name: "ClaimCore", exact: true }))
      .first()
      .waitFor({ timeout: 5_000 });
  } catch {
    const main = await page.evaluate(() => document.querySelector("main")?.className ?? "none");
    const kind = ["loading", "login-shell", "app-shell"].includes(main) ? main : "other";
    throw new Error(`E2E_LOGIN_VIEW_${kind.toUpperCase().replace(/-/gu, "_")}`);
  }
  if (await authenticated.isVisible()) {
    await progress("login-auth");
    await progress("login-ready");
    return;
  }
  await progress("login-anon");
  const applicationOrigin = new URL(page.url()).origin;
  const owner = await syntheticCredentials(username);
  await awaitLoginAdmission(page, signIn, applicationOrigin);
  await page.locator('input[name="username"]').or(authenticated).first().waitFor();
  if (await authenticated.isVisible()) {
    await progress("login-ready");
    return;
  }
  await progress("oidc-navigation");
  const issuerOrigin = new URL(page.url()).origin;
  const submitted = observeOidcSubmission(page, issuerOrigin, owner);
  await fillOidcCredentials(page, owner);
  const callbackStatus = trackOidcCallback(page, applicationOrigin);
  await page.locator('input[type="submit"], button[type="submit"]').first().click();
  await progress("oidc-submitted");
  await awaitOidcReturn(page, applicationOrigin, callbackStatus, submitted, issuerOrigin);
  await progress("oidc-callback-returned");
  await expect(authenticated).toBeVisible();
  await progress("login-ready");
};

export const logout = async (page: Page): Promise<void> => {
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/session.logout",
  );
  await page.getByRole("button", { name: "Sign out" }).click();
  const response = await responseEvent;
  expect(response.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  const reply = await browserRequest(page, "/api/session");
  if (!isResponse("session", reply.payload)) {
    throw new Error("Invalid logout response.");
  }
  if (reply.payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("Session refused.");
  }
  expect(reply.payload.outcome.data.authenticated).toBe(false);
};
