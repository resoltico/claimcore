import { freshLoginWorkflowTimeout } from "./admission-window";
import { expect, test, type Page } from "./fixtures";
import type { APIResponse } from "@playwright/test";
import { operationId } from "../src/operation/operationId";

import { consumerContractIdentity, type ResponseFor } from "@contracts/client";
import { recoveryRecords } from "./recovery-storage";
import { selectedActor, syntheticPrincipals } from "./people-setup";
import {
  awaitLoginAdmission,
  browserRequest,
  installationId,
  expectAccessible,
  login,
  logout,
  sessionToken,
  seedSignedOut,
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
  baseURL,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout * 2);
  if (baseURL === undefined) {
    throw new Error("Published browser acceptance requires its configured HTTPS base URL.");
  }
  const landing = new URL("/", baseURL);
  const admission = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/auth/login" &&
      response.request().isNavigationRequest(),
  );
  await page.goto(landing.href, { waitUntil: "commit" });
  if ((await admission).status() === 429) {
    // Another synthetic journey may have used the shared admission window.
    await page.waitForTimeout(61_000);
    await page.goto(landing.href, { waitUntil: "commit" });
  }
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
  await awaitLoginAdmission(page, "Sign in again", landing.origin).catch(() => {
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
  // Four independent logins can each wait for a new admission window.
  test.setTimeout(freshLoginWorkflowTimeout * 4);
  if (baseURL === undefined) {
    throw new Error("Published browser acceptance requires its configured HTTPS base URL.");
  }
  await seedSignedOut(page);
  await page.goto("/", { waitUntil: "commit" });
  await login(page, "Request access", "Sign in", "synthetic-outsider");
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/people-history");
  await expect(page.getByRole("heading", { name: "Request access" })).toBeVisible();
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
    await seedSignedOut(second);
    await second.goto("/", { waitUntil: "commit" });
    await login(second, "Request access", "Sign in", "synthetic-outsider");
    await signOut(page, page.getByRole("button", { name: "Sign out", exact: true }));
    await expectAnonymousSession(page);
    await expect(readSession(second)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    await login(page, "Request access", "Sign in", "synthetic-outsider");
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
    expect(response.request().postData()).toMatch(
      /^\{"input:operationId":"[0-9a-f-]{14}7[0-9a-f-]{21}"\}$/u,
    );
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
    await login(page, "Request access", "Sign in", "synthetic-outsider");
    await expect(readSession(page)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    await signOut(page, page.getByRole("button", { name: "Sign out", exact: true }));
    await expectAnonymousSession(page);
  } finally {
    await other.close();
  }
});

type TerminationEvidence = {
  outcome?: {
    document?: {
      screen?: {
        regions?: {
          content?: {
            entries?: { key?: string; value?: { value?: number } }[];
          };
        }[];
      };
    };
  };
};
const terminationCount = (payload: unknown) => {
  const evidence = payload as TerminationEvidence;
  const entry = evidence.outcome?.document?.screen?.regions
    ?.flatMap((region) => region.content?.entries ?? [])
    .find((item) => item.key === "count");
  return entry?.value?.value;
};

test("settles a committed lost termination after sign-in without ending the newer session @engine", async ({
  page,
}) => {
  test.setTimeout(2 * freshLoginWorkflowTimeout);
  await seedSignedOut(page);
  await page.goto("/", { waitUntil: "commit" });
  await login(page, "Request access", "Sign in", "synthetic-outsider");
  const action = "End my ClaimCore browser sessions";
  const context = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "ClaimCore-Representation": "HUMAN",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: "{}",
  });
  const current = context.payload as ResponseFor<"context", "HUMAN">;
  const requester = current.outcome.kind === "SUCCEEDED" ? current.outcome.document.subject : null;
  const scope = requester?.kind === "REQUESTER" ? requester.terminationScope : undefined;
  if (scope === undefined) {
    throw new Error("Expected the current session termination scope.");
  }
  // Earlier scenarios may have committed a cutoff. Seed IDs at the current window boundary.
  const lifetime = 10 * 60_000;
  const priorCutoff = scope.retirementCutoff === null ? 0 : Date.parse(scope.retirementCutoff) + 1;
  const issued = Math.max(Date.parse(scope.observedAt) - lifetime, priorCutoff);
  const staleAt = new Date(Date.parse(scope.observedAt) - 25 * 60_000)
    .toISOString()
    .replace("Z", "000Z");
  let checked = 0;
  await page.route("**/api/context", async (route) => {
    const bound = route.request().headers()["claimcore-installation"] === scope.installationId;
    if (bound) {
      checked += 1;
      if (checked > 2) {
        await route.continue();
        return;
      }
    }
    const response = await route.fetch();
    const payload = (await response.json()) as ResponseFor<"context", "HUMAN">;
    if (payload.outcome.kind === "SUCCEEDED") {
      const { document } = payload.outcome;
      if (
        document.subject.kind === "REQUESTER" &&
        document.subject.terminationScope !== undefined
      ) {
        const cutoff = Date.parse(document.subject.terminationScope.retirementCutoff ?? "");
        if (bound && checked === 1) {
          expect(Number.isNaN(cutoff) || cutoff <= issued).toBe(true);
          expect(Date.parse(document.subject.terminationScope.observedAt)).toBeGreaterThan(
            issued + lifetime,
          );
          await route.fulfill({ response });
          return;
        }
        if (bound) {
          // Step the observed clock back after the real PostgreSQL cutoff commits.
          expect(cutoff).toBeGreaterThan(issued);
        }
        await route.fulfill({
          response,
          json: {
            ...payload,
            outcome: {
              ...payload.outcome,
              document: {
                ...document,
                subject: {
                  ...document.subject,
                  terminationScope: { ...document.subject.terminationScope, observedAt: staleAt },
                },
              },
            },
          },
        });
        return;
      }
    }
    await route.fulfill({ response });
  });
  const recoveryKey = "claimcore.sessionTerminations.v1";
  const expiredTimestamp = issued.toString(16).padStart(12, "0");
  const foreign = (index: number) => ({
    serviceOrigin: new URL(page.url()).origin,
    installationId: scope.installationId,
    requesterKey: `10000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`,
    target: "self",
    id: `${expiredTimestamp.slice(0, 8)}-${expiredTimestamp.slice(8)}-7000-8000-${index.toString(16).padStart(12, "0")}`,
  });
  const expired = Array.from({ length: 16 }, (_, index) => foreign(index + 1));
  const live = { ...foreign(17), id: operationId() };
  const otherInstall = { ...foreign(19), installationId: "90000000-0000-4000-8000-000000000099" };
  const damaged = { ...foreign(18), id: "unreadable" };
  await page.evaluate(
    ({ key, records }) => {
      sessionStorage.setItem(key, JSON.stringify(records));
    },
    { key: recoveryKey, records: [...expired, live, damaged, otherInstall] },
  );
  await page.reload({ waitUntil: "commit" });
  await expect(page.getByText("Some saved termination requests could not be read.")).toBeVisible();
  await expect(page.getByText(live.requesterKey, { exact: false })).toHaveCount(0);

  const requests: string[] = [];
  let originalCount: number | undefined;
  let retirementCheck: unknown;
  await page.route("**/api/session.terminateOwn", async (route) => {
    const body = route.request().postData() ?? "";
    if (requests.length === 0 && body.includes('"input:confirmRetirement":true')) {
      retirementCheck = JSON.parse(body);
      const saved = await page.evaluate((key) => sessionStorage.getItem(key), recoveryKey);
      expect(JSON.parse(saved ?? "[]")).toEqual([...expired, live, damaged, otherInstall]);
      const reply = await route.fetch();
      expect(await reply.json()).toMatchObject({ outcome: { kind: "RETIREMENT_CONFIRMED" } });
      await route.fulfill({ response: reply });
      return;
    }
    requests.push(body);
    if (requests.length > 1) {
      await route.continue();
      return;
    }
    const retained = await page.evaluate((key) => sessionStorage.getItem(key), recoveryKey);
    const attempts: unknown = JSON.parse(retained ?? "[]");
    expect(attempts).toHaveLength(3);
    expect(attempts).toMatchObject([live, otherInstall, { target: "self" }]);
    const reply = await route.fetch();
    expect(reply.status()).toBe(200);
    const payload: unknown = await reply.json();
    expect(payload).toMatchObject({ outcome: { kind: "SUCCEEDED", self: true } });
    originalCount = terminationCount(payload);
    expect(originalCount).toBeGreaterThan(0);
    await route.abort();
  });
  const control = page.getByRole("button", { name: "Start a new termination", exact: true });
  await control.click();
  const dialog = page.getByRole("dialog", { name: action, exact: true });
  await expect(dialog).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  expect(requests).toHaveLength(0);
  await control.click();
  await dialog.getByRole("button", { name: action, exact: true }).click();
  await expect(dialog.getByRole("alert")).toHaveText(
    "The request may have ended browser sessions. Repeat it using the same request ID to recover the result. A new request ID can end newer sessions.",
  );
  await expect(dialog).not.toContainText("ClaimCore browser sessions ended.");
  await expect.poll(() => recoveryRecords(page, { allowMissing: true })).toEqual([]);
  expect(requests).toHaveLength(1);
  expect(checked).toBe(2);
  expect(retirementCheck).toEqual({ ...JSON.parse(requests[0]!), "input:confirmRetirement": true });
  await expectAnonymousSession(page);
  await page.reload({ waitUntil: "commit" });
  await login(page, "Request access", "Sign in", "synthetic-outsider");
  const resume = "Resume unconfirmed termination";
  await expect(
    page.getByRole("button", { name: "Start a new termination", exact: true }),
  ).toBeVisible();
  expect(requests).toHaveLength(1);
  await page.getByRole("button", { name: resume, exact: true }).click();
  await expect(dialog).toBeVisible();
  expect(requests).toHaveLength(1);
  const settlement = page.waitForResponse((reply) =>
    reply.url().endsWith("/api/session.terminateOwn"),
  );
  await dialog.getByRole("button", { name: resume, exact: true }).click();
  const confirmed = await settlement;
  expect(confirmed.status()).toBe(200);
  expect(terminationCount(await confirmed.json())).toBe(originalCount);
  expect(requests).toHaveLength(2);
  expect(checked).toBe(3);
  const original: unknown = JSON.parse(requests[0]!);
  if (typeof original !== "object" || original === null) {
    throw new Error("Expected the original termination request object.");
  }
  expect(JSON.parse(requests[1]!)).toEqual({ ...original, "input:settleOnly": true });
  await expect(page.getByText("ClaimCore browser sessions ended.")).toBeVisible();
  await expect(page.getByRole("button", { name: resume, exact: true })).toHaveCount(0);
  const remaining = await page.evaluate((key) => sessionStorage.getItem(key), recoveryKey);
  expect(JSON.parse(remaining ?? "[]")).toEqual([live, otherInstall]);
  await expect(readSession(page)).resolves.toMatchObject({
    payload: { outcome: { data: { authenticated: true } } },
  });
});

test("the installation header gates every own-session termination mode @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await seedSignedOut(page);
  await page.goto("/", { waitUntil: "commit" });
  await login(page, "Request access", "Sign in", "synthetic-outsider");
  const token = await sessionToken(page);
  const headers = { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token };
  const scope = async () => {
    const reply = await browserRequest(page, "/api/context", {
      method: "POST",
      headers,
      body: "{}",
    });
    const payload = reply.payload as ResponseFor<"context", "DATA">;
    if (
      payload.outcome.kind !== "SUCCEEDED" ||
      payload.outcome.data.terminationScope === undefined
    ) {
      throw new Error("Expected a termination scope for the unregistered principal.");
    }
    return payload.outcome.data.terminationScope;
  };
  const original = await scope();
  const identifier = operationId();
  const post = (mode: object, installation?: string) =>
    browserRequest(page, "/api/session.terminateOwn", {
      method: "POST",
      headers: {
        ...headers,
        ...(installation === undefined ? {} : { "ClaimCore-Installation": installation }),
      },
      body: JSON.stringify({ "input:operationId": identifier, ...mode }),
    });

  for (const mode of [{}, { "input:settleOnly": true }, { "input:confirmRetirement": true }]) {
    for (const invalid of [undefined, "invalid", "00000000-0000-0000-0000-000000000000"]) {
      const refused = await post(mode, invalid);
      expect(refused.status).toBe(400);
      expect(refused.payload).toMatchObject({
        hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" },
      });
    }
    const wrong = await post(mode, "10000000-0000-4000-8000-000000000001");
    expect(wrong.status).toBe(409);
    expect(wrong.payload).toMatchObject({
      hostFailure: { code: "INSTALLATION_MISMATCH", phase: "NOT_STARTED" },
    });
  }
  expect((await scope()).retirementCutoff).toBe(original.retirementCutoff);
  await expect(readSession(page)).resolves.toMatchObject({
    payload: { outcome: { data: { authenticated: true } } },
  });
  expect((await post({ "input:settleOnly": true }, original.installationId)).payload).toMatchObject(
    { outcome: { kind: "OUTCOME_UNKNOWN" } },
  );
  expect(
    (await post({ "input:confirmRetirement": true }, original.installationId)).payload,
  ).toMatchObject({ outcome: { kind: "RETIREMENT_CONFIRMED" } });
  expect((await scope()).retirementCutoff).not.toBe(original.retirementCutoff);
  const cut = (await post({}, original.installationId)).payload;
  expect(cut).toMatchObject({ outcome: { kind: "SUCCEEDED", data: { self: true } } });
  expect((cut as ResponseFor<"session.terminateOwn", "DATA">).outcome).toMatchObject({
    data: { count: expect.any(Number) },
  });
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
  await seedSignedOut(page);
  await page.goto("/");
  await login(page);
  const other = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    storageState: { cookies: [], origins: [] },
  });
  try {
    const person = await other.newPage();
    await seedSignedOut(person);
    await person.goto("/");
    await login(person, "Cases", "Sign in", "synthetic-steward");
    const principal = await syntheticPrincipals();
    const actorId = await selectedActor(page, {
      kind: "HUMAN",
      issuer: principal.issuer,
      subject: principal.stewardSubject,
    });
    const token = await sessionToken(page);
    const register = await installationId(page, token);
    const post = (mode: object, installation?: string) =>
      browserRequest(page, "/api/people.terminateSessions", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "ClaimCore-Representation": "HUMAN",
          "X-ClaimCore-Antiforgery": token,
          ...(installation === undefined ? {} : { "ClaimCore-Installation": installation }),
        },
        body: JSON.stringify({
          "input:actorId": actorId,
          "input:operationId": operationId(),
          ...mode,
        }),
      });
    for (const mode of [{}, { "input:settleOnly": true }, { "input:confirmRetirement": true }]) {
      expect((await post(mode)).payload).toMatchObject({ hostFailure: { code: "INPUT_INVALID" } });
      expect((await post(mode, "invalid")).payload).toMatchObject({
        hostFailure: { code: "INPUT_INVALID" },
      });
      expect((await post(mode, "10000000-0000-4000-8000-000000000001")).payload).toMatchObject({
        hostFailure: { code: "INSTALLATION_MISMATCH" },
      });
    }
    await expect(readSession(person)).resolves.toMatchObject({
      payload: { outcome: { data: { authenticated: true } } },
    });
    const replay = await post({}, register);
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
