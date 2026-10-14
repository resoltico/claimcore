import { wireContractFingerprint } from "../src/generated/contracts/endpoint-catalog";
import { expect, test, type Page } from "@playwright/test";

import { browserRequest, expectHostFailure, login, sessionToken } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const rejectMalformedAuthenticatedPosts = async (page: Page, token: string): Promise<void> => {
  const listPath = "/api/case.list";
  await expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "text/plain", "X-ClaimCore-Antiforgery": token },
      body: '{"limit":1}',
    }),
    415,
    "MEDIA_TYPE_REJECTED",
  );
  // Large uploads can abort a browser fetch before it exposes the early rejection body.
  // The HTTP client observes the actual default-size admission response, using this session.
  const backend = process.env["CLAIMCORE_WEB_E2E_BACKEND_URL"];
  if (backend === undefined) {
    throw new Error("Published admission fixture is missing.");
  }
  const oversized = await page.request.post(backend + listPath, {
    headers: {
      "Content-Type": "application/json",
      Origin: new URL(page.url()).origin,
      Host: new URL(page.url()).host,
      "X-ClaimCore-Antiforgery": token,
      "ClaimCore-Contract": wireContractFingerprint,
    },
    data: "x".repeat(16 * 1024 * 1024 + 1),
    maxRedirects: 0,
  });
  await expectHostFailure(
    {
      status: oversized.status(),
      cacheControl: oversized.headers()["cache-control"] ?? null,
      payload: await oversized.json(),
    },
    413,
    "BODY_TOO_LARGE",
  );
  await expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
      body: '{"limit":',
    }),
    400,
    "INPUT_INVALID",
  );
};

test("rejects absent routes and malformed published Web service admission before mutation", async ({
  page,
}) => {
  await page.goto("/", { waitUntil: "commit" });
  await expectHostFailure(await browserRequest(page, "/api/not-present"), 404, "ENDPOINT_MISSING");
  await expectHostFailure(
    await browserRequest(page, "/api/case.list", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: '{"limit":1}',
    }),
    401,
    "SESSION_REJECTED",
  );
  await login(page);
  const token = await sessionToken(page);
  await expectHostFailure(
    await browserRequest(page, "/api/case.list", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: '{"limit":1}',
    }),
    403,
    "ANTIFORGERY_REJECTED",
  );
  await rejectMalformedAuthenticatedPosts(page, token);
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
});
