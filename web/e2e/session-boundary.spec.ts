import { freshLoginWorkflowTimeout } from "./admission-window";
import { expect, test, type Page } from "@playwright/test";

import { consumerContractIdentity } from "@contracts/client";
import { recoveryRecords } from "./recovery-storage";
import { selectedActor, syntheticPrincipals } from "./people-setup";
import { browserRequest, expectAccessible, login, logout, sessionToken } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

type SessionEnvelope = {
  outcome: { kind: "SUCCEEDED"; data: { authenticated: boolean; antiforgeryToken: string } };
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
    payload: {
      outcome: {
        view: { regions: expect.any(Array) },
        kind: "SUCCEEDED",
        data: { authenticated: false },
      },
    },
  });
};

test("uses OIDC sign-in and the accessible actor-bound case workspace @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.goto("/", { waitUntil: "commit" });
  await expect(
    page.getByRole("heading", { name: "Sign in to the case register", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  const antiforgeryToken = await expectAnonymousSessionBoundary(page);
  await expectSessionRejection(page, antiforgeryToken);
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
  await logout(page);
  await expectAnonymousSession(page);
});

test("a refused callback offers a fresh same-origin restart without copying the callback @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.goto("/cases?view=ALL", { waitUntil: "commit" });
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  await page.evaluate(() => {
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
    await page.goto("/");
    await page.evaluate(() => {
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
    const firstLogout = page.waitForResponse((response) =>
      response.url().endsWith("/api/session.logout"),
    );
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    expect((await firstLogout).status()).toBe(200);
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
    await expect(
      page.getByRole("heading", { name: "Sign in to the case register", exact: true }),
    ).toBeVisible();
    await expectAnonymousSession(page);
    await expectAnonymousSession(second);
    await second.reload({ waitUntil: "commit" });
    await expect(
      second.getByRole("heading", { name: "Sign in to the case register", exact: true }),
    ).toBeVisible();
    // ClaimCore deletion did not revoke the identity-provider session or prevent later login.
    await login(page, "You don’t have access yet", "Sign in", "synthetic-outsider");
    await expect(readSession(page)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    const signedOut = page.waitForResponse(
      (reply) => new URL(reply.url()).pathname === "/api/session.logout",
    );
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    expect((await signedOut).status()).toBe(200);
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
