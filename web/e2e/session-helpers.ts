import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

import { AxeBuilder } from "@axe-core/playwright";
import { expect, type BrowserContext, type Page } from "@playwright/test";

import { isHostFailure, isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import type { HostFailure, WebV2Response } from "../src/generated/convergence/web-v2.types";

type BrowserReply = Readonly<{
  status: number;
  cacheControl: string | null;
  payload: unknown;
}>;

const credentialFile = process.env["CLAIMCORE_WEB_BOOTSTRAP_CREDENTIAL_FILE"];
if (credentialFile === undefined) throw new Error("Browser credential file was not configured.");
type Cookies = Awaited<ReturnType<BrowserContext["cookies"]>>;

export const openAuthenticated = async (page: Page): Promise<void> => {
  const output = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (output === undefined) throw new Error("Private browser output was not configured.");
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
  await page.goto("/", { waitUntil: "domcontentloaded", timeout: 10_000 });
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
};

export const expectAccessible = async (page: Page): Promise<void> => {
  const result = await new AxeBuilder({ page }).analyze();
  if (result.violations.length === 0) return;
  const ruleIds = result.violations
    .map((violation) => violation.id.replace(/[^a-zA-Z0-9]+/gu, "_").toUpperCase())
    .sort()
    .join("__");
  throw new Error(`E2E_AXE_${ruleIds}`);
};

export const browserRequest = async (
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
  const reply = await browserRequest(page, "/api/v2/session");
  if (!(await isWebV2Response("session", reply.payload))) {
    throw new Error("Invalid session response.");
  }
  const snapshot = (reply.payload as WebV2Response<"session">).outcome.data;
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
  const credential = (await readFile(credentialFile, "utf8")).trim();
  await page.getByLabel("Bootstrap credential").fill(credential);
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v2/session/login",
    { timeout: 5_000 },
  );
  await page.getByRole("button", { name: "Sign in" }).click();
  const response = await responseEvent;
  if (response.status() === 429) throw new Error("E2E_LOGIN_HTTP_THROTTLED");
  if (response.status() !== 200) throw new Error("E2E_LOGIN_HTTP_REJECTED");
  await expect(cases).toBeVisible();
  await progress("login-ready");
};

export const logout = async (page: Page): Promise<void> => {
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v2/session/logout",
  );
  await page.getByRole("button", { name: "Sign out" }).click();
  const response = await responseEvent;
  expect(response.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const reply = await browserRequest(page, "/api/v2/session");
  if (!(await isWebV2Response("session", reply.payload))) {
    throw new Error("Invalid logout response.");
  }
  expect((reply.payload as WebV2Response<"session">).outcome.data.authenticated).toBe(false);
};

export const rejectedLogin = (page: Page, token: string): Promise<BrowserReply> =>
  browserRequest(page, "/api/v2/session/login", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
    },
    body: JSON.stringify({
      credential: "invalid-bootstrap-credential",
      antiforgeryToken: token,
    }),
  });

export const progress = async (stage: string): Promise<void> => {
  const file = process.env["CLAIMCORE_WEB_E2E_PROGRESS_FILE"];
  if (file !== undefined) await import("node:fs/promises").then((fs) => fs.writeFile(file, stage));
};
