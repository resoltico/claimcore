import { expect, test, type Page } from "@playwright/test";

import { browserRequest, expectHostFailure, login, sessionToken } from "./session-helpers";

test.use({ storageState: { cookies: [], origins: [] } });

const rejectMalformedAuthenticatedPosts = async (page: Page, token: string): Promise<void> => {
  const listPath = "/api/v2/cases/list";
  await expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "text/plain", "X-ClaimCore-Antiforgery": token },
      body: '{"limit":1}',
    }),
    415,
    "WEB_MEDIA_TYPE",
  );
  await expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
      body: "x".repeat(65540),
    }),
    413,
    "WEB_BODY_TOO_LARGE",
  );
  await expectHostFailure(
    await browserRequest(page, listPath, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
      body: '{"limit":',
    }),
    400,
    "WEB_INVALID_REQUEST",
  );
};

test("rejects retired routes and malformed published Web v2 admission before mutation", async ({
  page,
}) => {
  await page.goto("/");
  await expectHostFailure(await browserRequest(page, "/api/v1/query"), 404, "WEB_NOT_FOUND");
  await expectHostFailure(
    await browserRequest(page, "/api/v2/cases/list", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: '{"limit":1}',
    }),
    401,
    "WEB_SESSION_REJECTED",
  );
  await login(page);
  const token = await sessionToken(page);
  await expectHostFailure(
    await browserRequest(page, "/api/v2/cases/list", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: '{"limit":1}',
    }),
    403,
    "WEB_CSRF_REJECTED",
  );
  await rejectMalformedAuthenticatedPosts(page, token);
  await expect(page.getByRole("heading", { name: "Cases" })).toBeVisible();
});
