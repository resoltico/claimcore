import { freshLoginWorkflowTimeout } from "./admission-window";
import { expect, test, type Page } from "./fixtures";
import type { APIResponse } from "@playwright/test";

import { consumerContractIdentity } from "@contracts/client";
import { recoveryRecords } from "./recovery-storage";
import { selectedActor, syntheticPrincipals } from "./people-setup";
import {
  awaitLoginAdmission,
  browserRequest,
  expectAccessible,
  login,
  logout,
  sessionToken,
  signOut,
} from "./session-helpers";

// A logout navigation carries an ID token hint in the provider request URL.
test.use({ storageState: { cookies: [], origins: [] }, trace: "off" });

type SessionEnvelope = {
  outcome: { kind: "SUCCEEDED"; data: { authenticated: boolean; antiforgeryToken: string } };
};

type HostFailure = { hostFailure: { code: string; phase: string | null } };
type ApiReply<T> = { status: number; cacheControl: string | null; payload: T };

const apiReply = async <T>(response: APIResponse): Promise<ApiReply<T>> => ({
  status: response.status(),
  cacheControl: response.headers()["cache-control"] ?? null,
  payload: (await response.json()) as T,
});

const readSession = async (page: Page) =>
  apiReply<SessionEnvelope>(
    await page.request.get("/api/session", {
      headers: { "ClaimCore-Contract": consumerContractIdentity },
    }),
  );

const readUnknownEndpoint = async (page: Page) =>
  apiReply<HostFailure>(await page.request.get("/api/service/not-present"));

const readProtectedList = async (page: Page, token: string, origin: string) =>
  apiReply<HostFailure>(
    await page.request.post("/api/case.list", {
      headers: {
        "ClaimCore-Contract": consumerContractIdentity,
        "X-ClaimCore-Antiforgery": token,
        Origin: origin,
      },
      data: { limit: 1 },
    }),
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
    payload: {
      outcome: {
        view: { regions: expect.any(Array) },
        kind: "SUCCEEDED",
        data: { authenticated: false },
      },
    },
  });
  return session.payload.outcome.data.antiforgeryToken;
};

const expectSessionRejection = async (page: Page, token: string, origin: string) => {
  const rejected = await readProtectedList(page, token, origin);
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
    payload: {
      outcome: {
        view: { regions: expect.any(Array) },
        kind: "SUCCEEDED",
        data: { authenticated: false },
      },
    },
  });
};

const checkSignOutLocation = async (
  reply: APIResponse,
  providerRequest: string,
  landing: URL,
  cookies: string,
) => {
  const payload = (await reply.json().catch(() => {
    throw new Error("The Sign out reply was not JSON.");
  })) as { outcome: { data: { location: string } } };
  const { location, ...otherData } = payload.outcome.data;
  const address = URL.parse(location);
  const hint = address?.searchParams.get("id_token_hint");
  if (address === null || hint === undefined || hint === null || hint === "") {
    throw new Error("The Sign out reply did not carry the provider's ID token hint.");
  }
  expect(providerRequest === location, "The browser navigated to the returned location").toBe(true);
  expect(address.searchParams.get("client_id") === "web").toBe(true);
  expect(address.searchParams.get("post_logout_redirect_uri") === landing.href).toBe(true);
  expect(reply.headers()["cache-control"]).toContain("no-store");
  expect(reply.headers()["referrer-policy"]).toBe("no-referrer");
  const otherBody = { ...payload, outcome: { ...payload.outcome, data: otherData } };
  expect(JSON.stringify(otherBody).includes(hint)).toBe(false);
  expect(JSON.stringify(reply.headersArray()).includes(hint)).toBe(false);
  expect(cookies.includes(hint)).toBe(false);
};

test("signs out of ClaimCore and Keycloak before asking for credentials again @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout * 2);
  await page.goto("/", { waitUntil: "commit" });
  const landing = new URL("/", page.url());
  await expect(page.locator('input[name="username"]')).toBeVisible();
  const antiforgeryToken = await expectAnonymousSessionBoundary(page);
  await expectSessionRejection(page, antiforgeryToken, landing.origin);
  await expectAbsentRoute(page);
  await login(page);
  await expect(page.getByRole("heading", { name: "Cases", level: 1 })).toBeVisible();
  await expect(readSession(page)).resolves.toMatchObject({
    status: 200,
    payload: {
      outcome: {
        view: { regions: expect.any(Array) },
        kind: "SUCCEEDED",
        data: { authenticated: true },
      },
    },
  });
  await expectAccessible(page);
  const { issuer } = await syntheticPrincipals();
  const endpoint = new URL(`${issuer}/protocol/openid-connect/logout`);
  const before = await page.context().cookies();
  expect(before.some((cookie) => cookie.name === "KEYCLOAK_IDENTITY")).toBe(true);
  // Capture the network reply independently of the document WebKit unloads on sign-out.
  const captured = Promise.withResolvers<APIResponse>();
  await page.route(
    (url) => url.origin === landing.origin && url.pathname === "/api/session.logout",
    async (route) => {
      try {
        const response = await route.fetch({ maxRedirects: 0 });
        await route.fulfill({ response });
        captured.resolve(response);
      } catch {
        captured.reject(new Error("The Sign out reply could not be captured."));
      }
    },
    { times: 1 },
  );
  const [reply, endSession] = await Promise.all([
    captured.promise,
    page.waitForRequest((request) => {
      const url = new URL(request.url());
      return (
        request.isNavigationRequest() &&
        request.frame() === page.mainFrame() &&
        url.origin === endpoint.origin &&
        url.pathname === endpoint.pathname
      );
    }),
    logout(page),
  ]);
  const after = await page.context().cookies();
  expect(after.some((cookie) => cookie.name === "KEYCLOAK_IDENTITY")).toBe(false);
  await checkSignOutLocation(
    reply,
    endSession.url(),
    landing,
    JSON.stringify([...before, ...after]),
  );
  await expectAnonymousSession(page);

  // Stop any unexpected direct callback before it can create a new application session.
  await page.route(
    (url) => url.origin === landing.origin && url.pathname === "/signin-oidc",
    (route) => route.abort(),
  );
  await awaitLoginAdmission(page, "Sign in", landing.origin).catch(() => {
    throw new Error("Keycloak did not present a fresh sign-in challenge.");
  });
  await expect(page.locator('input[name="username"]')).toBeVisible();
  await expect(page.locator('input[name="password"]')).toBeVisible();
  expect(new URL(page.url()).origin === endpoint.origin).toBe(true);
});

test("a refused callback offers a fresh same-origin restart without copying the callback @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.addInitScript(() => {
    if (location.pathname !== "/signin-oidc") {
      return;
    }
    sessionStorage.setItem("claimcore.loginReturn", "/cases?view=ALL");
    localStorage.setItem(
      "claimcore.presentation.v3",
      JSON.stringify({
        version: 3,
        language: "lv",
        displayLocale: "en-GB",
        mode: "system",
        accent: "blue",
      }),
    );
  });
  const canary = "CALLBACK-PRIVATE-CANARY";
  const refused = await page.goto(`/signin-oidc?error=access_denied&error_description=${canary}`);
  expect(refused?.status()).toBe(401);
  expect(refused?.headers()["cache-control"]).toContain("no-store");
  expect(refused?.headers()["referrer-policy"]).toBe("no-referrer");
  await expect(page.locator("html")).toHaveAttribute("lang", "lv");
  await expect(page.locator("#restart-sign-in")).toHaveAttribute("href", "/auth/login?language=lv");
  await expect(page.locator("body")).not.toContainText(canary);
  expect(await page.evaluate(() => sessionStorage.getItem("claimcore.loginReturn"))).toBe(
    "/cases?view=ALL",
  );
  await expectAnonymousSession(page);
  await login(page, null, (await page.locator("#restart-sign-in").textContent())!);
  await expect(page).toHaveURL(/\/cases\?view=ALL$/u);
  await expect(page.locator("html")).toHaveAttribute("lang", "lv");
  await expect(readSession(page)).resolves.toMatchObject({
    payload: { outcome: { data: { authenticated: true } } },
  });
});

for (const dependency of ["catalog", "script"] as const) {
  test(`a refused callback retains its static retry when only the ${dependency} fails @engine`, async ({
    page,
  }) => {
    await page.addInitScript(() => {
      if (location.pathname !== "/signin-oidc") {
        return;
      }
      sessionStorage.removeItem("claimcore.loginReturn");
      localStorage.setItem(
        "claimcore.presentation.v3",
        JSON.stringify({
          version: 3,
          language: "lv",
          displayLocale: "en-GB",
          mode: "system",
          accent: "blue",
        }),
      );
    });
    let blocked = 0;
    const glob = dependency === "catalog" ? "**/catalogs/*.json" : "**/assets/*.js";
    await page.route(glob, async (route) => {
      blocked += 1;
      await route.abort();
    });
    const refused = await page.goto("/signin-oidc?error=access_denied");
    expect(refused?.status()).toBe(401);
    await expect.poll(() => blocked).toBeGreaterThan(0);
    await expect(page.getByRole("link", { name: "Restart sign-in" })).toHaveAttribute(
      "href",
      "/auth/login",
    );
    expect(await page.evaluate(() => sessionStorage.getItem("claimcore.loginReturn"))).toBeNull();
  });
}

test("ends all ClaimCore browser sessions while allowing a fresh login afterward @engine", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout * 2);
  if (baseURL === undefined) {
    throw new Error("Published browser acceptance requires its configured HTTPS base URL.");
  }
  await page.goto("/", { waitUntil: "commit" });
  await login(page, "You don’t have access yet", "Sign in", "synthetic-outsider");
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/people-history");
  await expect(page.getByRole("heading", { name: "You don’t have access yet" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Access history" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Search cases" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible();
  const other = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    storageState: { cookies: [], origins: [] },
  });
  try {
    const second = await other.newPage();
    await second.goto("/", { waitUntil: "commit" });
    await login(second, "You don’t have access yet", "Sign in", "synthetic-outsider");
    await signOut(page, page.getByRole("button", { name: "Sign out", exact: true }));
    await expectAnonymousSession(page);
    await expect(readSession(second)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    await login(page, "You don’t have access yet", "Sign in", "synthetic-outsider");
    await page
      .getByRole("button", { name: "End my ClaimCore browser sessions", exact: true })
      .click();
    const dialog = page.getByRole("dialog", {
      name: "End my ClaimCore browser sessions",
      exact: true,
    });
    await expect(dialog).toContainText(
      "Identity-provider sessions, credentials and independent bearer tokens remain valid.",
    );
    const acknowledgement = page.waitForResponse((response) =>
      response.url().endsWith("/api/session.terminateOwn"),
    );
    await dialog
      .getByRole("button", { name: "End my ClaimCore browser sessions", exact: true })
      .click();
    const response = await acknowledgement;
    expect(response.status()).toBe(200);
    expect(response.headers()["cache-control"]).toContain("no-store");
    expect(response.request().postDataJSON()).toEqual({});
    const payload: unknown = await response.json();
    expect(payload).toMatchObject({
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        self: true,
        document: { screen: { regions: expect.any(Array) } },
      },
    });
    expect(payload).not.toHaveProperty("operationId");
    await expectAnonymousSession(page);
    await expectAnonymousSession(second);
    await second.route("**/auth/login?*", (route) => route.fulfill({ status: 204 }));
    await second.reload({ waitUntil: "commit" });
    await expectAnonymousSession(second);
    // ClaimCore deletion did not revoke the identity-provider session or prevent later login.
    await login(page, "You don’t have access yet", "Sign in", "synthetic-outsider");
    await expect(readSession(page)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    await signOut(page, page.getByRole("button", { name: "Sign out", exact: true }));
    await expectAnonymousSession(page);
  } finally {
    await other.close();
  }
});

test("reports a dispatched session termination with a lost reply only once @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.goto("/", { waitUntil: "commit" });
  await login(page, "You don’t have access yet", "Sign in", "synthetic-outsider");
  const action = "End my ClaimCore browser sessions";
  let sent = 0;
  await page.route("**/api/session.terminateOwn", async (route) => {
    sent += 1;
    const reply = await route.fetch();
    expect(reply.status()).toBe(200);
    expect(await reply.json()).toMatchObject({ outcome: { kind: "SUCCEEDED", self: true } });
    await route.abort();
  });
  const control = page.getByRole("button", { name: action, exact: true });
  await control.click();
  const dialog = page.getByRole("dialog", { name: action, exact: true });
  await expect(dialog).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  expect(sent).toBe(0);
  await control.click();
  await dialog.getByRole("button", { name: action, exact: true }).click();
  await expect(dialog.getByRole("alert")).toHaveText(
    "The request was not confirmed. It may have ended the ClaimCore browser sessions. Repeating it can also end newer sessions.",
  );
  await expect(dialog).not.toContainText("ClaimCore browser sessions ended.");
  await expect.poll(() => recoveryRecords(page, { allowMissing: true })).toEqual([]);
  expect(sent).toBe(1);
  await expectAnonymousSession(page);
});

test("owner termination ends the selected person's existing ticket and preserves the administrator's @engine", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout * 2);
  if (baseURL === undefined) {
    throw new Error("Published browser acceptance requires its configured HTTPS base URL.");
  }
  await page.goto("/");
  await login(page);
  const other = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    storageState: { cookies: [], origins: [] },
  });
  try {
    const person = await other.newPage();
    await person.goto("/");
    await login(person, "Cases", "Sign in", "synthetic-steward");
    const principal = await syntheticPrincipals();
    const actorId = await selectedActor(page, {
      kind: "HUMAN",
      issuer: principal.issuer,
      subject: principal.stewardSubject,
    });
    const replay = await browserRequest(page, "/api/people.terminateSessions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "ClaimCore-Representation": "HUMAN",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: JSON.stringify({ "input:actorId": actorId }),
    });
    expect(replay.status).toBe(200);
    expect(replay.payload).toMatchObject({ outcome: { kind: "SUCCEEDED", self: false } });
    await expectAnonymousSession(person);
    await expect(readSession(page)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
  } finally {
    await other.close();
  }
});
