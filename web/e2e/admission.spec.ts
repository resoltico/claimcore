import { isResponse } from "../src/protocol/client";
import { consumerContractIdentity } from "@contracts/client";
import { expect, test, type Page } from "@playwright/test";

import { browserRequest, expectHostFailure, login, sessionToken } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const requestBodyBoundary = async (page: Page, token: string): Promise<void> => {
  const backend = process.env["CLAIMCORE_WEB_E2E_BACKEND_URL"];
  if (backend === undefined) {
    throw new Error("Published admission fixture is missing.");
  }
  const options = {
    headers: {
      "Content-Type": "application/json",
      Origin: new URL(page.url()).origin,
      Host: new URL(page.url()).host,
      "X-ClaimCore-Antiforgery": token,
      "ClaimCore-Contract": consumerContractIdentity,
    },
    maxRedirects: 0,
  };
  const maximumRequestBytes = 64 * 1024;
  const input = '{"limit":1}';
  const admitted = input + " ".repeat(maximumRequestBytes - Buffer.byteLength(input, "utf8"));
  expect(Buffer.byteLength(admitted, "utf8")).toBe(maximumRequestBytes);
  const listUrl = `${backend}/api/case.list`;
  const withinLimit = await page.request.post(listUrl, {
    ...options,
    data: admitted,
  });
  expect(withinLimit.status()).toBe(200);
  const value: unknown = await withinLimit.json();
  expect(isResponse("case.list", value)).toBe(true);
  if (!isResponse("case.list", value)) {
    throw new Error("Expected a typed case list at the request byte boundary.");
  }
  expect(value.outcome.tag).toBe("SUCCEEDED");
  const overLimitBody = `${admitted} `;
  expect(Buffer.byteLength(overLimitBody, "utf8")).toBe(maximumRequestBytes + 1);
  const oversized = await page.request.post(listUrl, {
    ...options,
    data: overLimitBody,
  });
  expectHostFailure(
    {
      status: oversized.status(),
      cacheControl: oversized.headers()["cache-control"] ?? null,
      payload: await oversized.json(),
    },
    413,
    "BODY_TOO_LARGE",
  );
};

const rejectMalformedAuthenticatedPosts = async (page: Page, token: string): Promise<void> => {
  const listPath = "/api/case.list";
  expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "text/plain", "X-ClaimCore-Antiforgery": token },
      body: '{"limit":1}',
    }),
    415,
    "MEDIA_TYPE_REJECTED",
  );
  await requestBodyBoundary(page, token);
  expectHostFailure(
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
  expectHostFailure(await browserRequest(page, "/api/not-present"), 404, "ENDPOINT_MISSING");
  expectHostFailure(
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
  expectHostFailure(
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
