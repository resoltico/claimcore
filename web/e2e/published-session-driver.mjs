import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import process from "node:process";
import { chromium } from "@playwright/test";

const [root, origin, owned, phase, name] = process.argv.slice(2);
const modes = new Set(["login", "verify", "logout", "stale"]);
const statePath = path.join(owned, "published-session-state.json");
const require = (condition, message) => {
  if (!condition) {
    throw new Error(message);
  }
};
const saved = () => JSON.parse(readFileSync(statePath, "utf8"));
const retain = (value) =>
  writeFileSync(path.join(owned, `${name}.json`), JSON.stringify(value), { mode: 0o600 });
const headers = (fingerprint, csrf, cookie) => ({
  "ClaimCore-Contract": fingerprint,
  Origin: origin,
  ...(csrf === undefined ? {} : { "X-ClaimCore-Antiforgery": csrf }),
  ...(cookie === undefined ? {} : { Cookie: cookie }),
});
const session = async (context, fingerprint, cookie) => {
  const response = await context.request.get(`${origin}/api/session`, {
    headers: headers(fingerprint, undefined, cookie),
  });
  const reply = await response.json();
  require(response.status() === 200 && reply.outcome.tag === "SUCCEEDED", "Session unavailable");
  return reply.outcome.data;
};
const contextRequest = (context, fingerprint, csrf, cookie) =>
  context.request.post(`${origin}/api/context`, {
    headers: headers(fingerprint, csrf, cookie),
    data: {},
  });
const protectedContext = async (context, fingerprint, csrf) => {
  const response = await contextRequest(context, fingerprint, csrf);
  const reply = await response.json();
  require(response.status() === 200 &&
    reply.outcome.tag === "SUCCEEDED", "Protected context refused");
  require(reply.outcome.data.capabilities.includes("MANAGE_AUTHORITY"), "Owner context missing");
};
const login = async (context, fingerprint) => {
  const credentials = JSON.parse(readFileSync(path.join(owned, "credentials.json"), "utf8"));
  const [owner] = credentials.users;
  require(typeof owner.username === "string" &&
    typeof owner.password === "string", "Credentials missing");
  const page = await context.newPage();
  // Redirect chains can bypass a homepage route. Block the separate application script request
  // so React cannot race this API-session probe's own cookie/antiforgery capture.
  let blockedScripts = 0;
  await page.route(
    (url) => url.origin === origin,
    (route) => {
      if (route.request().resourceType() === "script") {
        blockedScripts += 1;
        return route.abort();
      }
      return route.continue();
    },
  );
  await page.goto(`${origin}/auth/login`);
  await page.locator('input[name="username"]').fill(owner.username);
  await page.locator('input[name="password"]').fill(owner.password);
  await page.locator('input[type="submit"], button[type="submit"]').first().click();
  await page.waitForURL((url) => url.origin === origin && url.pathname === "/");
  require(blockedScripts > 0, "Application bootstrap was not isolated");
  const current = await session(context, fingerprint);
  require(current.authenticated && typeof current.antiforgeryToken === "string", "Login refused");
  await protectedContext(context, fingerprint, current.antiforgeryToken);
  const cookies = await context.cookies();
  const cookie = cookies
    .filter((value) => value.name.startsWith("__Host-ClaimCore"))
    .map((value) => `${value.name}=${value.value}`)
    .join("; ");
  writeFileSync(statePath, JSON.stringify({ cookies, csrf: current.antiforgeryToken, cookie }), {
    mode: 0o600,
  });
  return { authenticated: true, protectedStatus: 200 };
};
const probe = async (context, fingerprint, state) => {
  const current = await session(context, fingerprint, state.cookie);
  if (phase === "stale") {
    require(!current.authenticated, "Deleted session authenticated");
    const refused = await contextRequest(
      context,
      fingerprint,
      current.antiforgeryToken,
      state.cookie,
    );
    const reply = await refused.json();
    require(refused.status() === 401 &&
      reply.hostFailure?.code === "SESSION_REJECTED", "Stale cookie admitted");
    return { authenticated: false, protectedStatus: 401 };
  }
  require(current.authenticated, "Retained session missing");
  await protectedContext(context, fingerprint, state.csrf);
  if (phase === "logout") {
    const response = await context.request.post(`${origin}/api/session.logout`, {
      headers: headers(fingerprint, state.csrf),
      data: {},
    });
    const reply = await response.json();
    require(response.status() === 200 && reply.outcome.tag === "SUCCEEDED", "Logout refused");
  }
  return { authenticated: true, protectedStatus: 200 };
};
const run = async () => {
  require(modes.has(phase) && /^[a-z-]+$/u.test(name), "Invalid phase");
  const { consumerContractIdentity } = await import(path.join(root, "contracts/client.ts"));
  const state = phase === "login" ? undefined : saved();
  const browser = await chromium.launch({ headless: true });
  try {
    // Only the owned synthetic browser ignores fixture certificates; fixture readiness verifies its CA.
    const context = await browser.newContext({
      ignoreHTTPSErrors: true,
      ...(state === undefined ? {} : { storageState: { cookies: state.cookies, origins: [] } }),
    });
    context.setDefaultTimeout(15_000);
    const result =
      phase === "login"
        ? await login(context, consumerContractIdentity)
        : await probe(context, consumerContractIdentity, state);
    retain({ phase, ...result, completed: true });
  } finally {
    await browser.close();
  }
};
try {
  await run();
} catch (error) {
  process.stderr.write(
    `PUBLISHED_SESSION_${modes.has(phase) ? phase.toUpperCase() : "INVALID"}_${error?.name ?? "ERROR"}\n`,
  );
  process.exitCode = 1;
}
