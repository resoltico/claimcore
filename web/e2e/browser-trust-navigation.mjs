import assert from "node:assert/strict";
import { chromium } from "@playwright/test";
import { createConnection, createServer } from "node:net";

/** TCP forwarding preserves the real origin, Host and TLS identity. @param {number} port @param {string} address */
export async function forward(port, address) {
  const server = createServer((client) => {
    const upstream = createConnection({ host: address, port });
    client.on("error", () => upstream.destroy());
    upstream.on("error", () => client.destroy());
    client.on("close", () => upstream.destroy());
    upstream.on("close", () => client.destroy());
    client.pipe(upstream).pipe(client);
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => resolve(undefined));
  });
  return () =>
    new Promise((resolve) => {
      server.close(() => resolve(undefined));
    });
}

/** A new browser process prevents cached TLS sessions from proving a trust change. @param {(page: import("@playwright/test").Page) => Promise<void>} action */
async function freshBrowser(action) {
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: false });
    const page = await context.newPage();
    await action(page);
    return browser.version();
  } finally {
    await browser.close();
  }
}

/** @param {string} origin @param {string} error */
export const refused = (origin, error) =>
  freshBrowser(async (page) => {
    await assert.rejects(
      page.goto(origin),
      (reason) => reason instanceof Error && reason.message.includes(`net::${error}`),
    );
  });

/** @param {string} password */
export const authenticate = (password) =>
  freshBrowser(async (page) => {
    await page.goto("https://app.localhost:5443/");
    await page.getByRole("link", { name: "Sign in", exact: true }).click();
    assert.equal(new URL(page.url()).origin, "https://identity.localhost:5444");
    await page.locator('input[name="username"]').fill("owner");
    await page.locator('input[name="password"]').fill(password);
    await page.locator('input[type="submit"], button[type="submit"]').first().click();
    await page.waitForURL(
      (url) => url.origin === "https://app.localhost:5443" && url.pathname === "/",
    );
    const authenticated = await page.evaluate(async () => {
      const response = await fetch("/api/v3/session");
      /** @param {unknown} value @returns {value is Record<string, unknown>} */
      const record = (value) =>
        value !== null && typeof value === "object" && !Array.isArray(value);
      /** @type {unknown} */
      const payload = await response.json();
      if (response.status !== 200 || !record(payload)) {
        return false;
      }
      const { endpoint, outcome } = payload;
      return (
        endpoint === "session" &&
        record(outcome) &&
        record(outcome["data"]) &&
        outcome["data"]["authenticated"] === true
      );
    });
    assert.equal(authenticated, true);
  });
