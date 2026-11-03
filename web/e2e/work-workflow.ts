import { expect, type Page } from "@playwright/test";
import { endpoints, isResponse, type RequestByEndpoint } from "@contracts/client";
import { browserRequest, installationId, sessionToken } from "./session-helpers";
import { retainResponseBodies } from "./response-bodies";
import { isHostFailure } from "../src/protocol/client";
import { expectCaseReadsSettled } from "./case-readiness";

export const recordWorkReview = async (page: Page, family: "WORK" | "REMINDER") => {
  await retainResponseBodies(page);
  const label =
    family === "WORK"
      ? "I confirm these work changes."
      : "I confirm this reminder operation and its stated effects.";
  await page.getByRole("checkbox", { name: label, exact: true }).check();
  const response = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.execute",
  );
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  const payload: unknown = await (await response).json();
  if (
    !isResponse("operation.execute", payload) ||
    (payload.outcome.kind !== "ACCEPTED" && payload.outcome.kind !== "ALREADY_ACCEPTED") ||
    payload.outcome.data.subject.kind !== family
  ) {
    throw new Error("E2E_WORK_SUBMIT_FAILED");
  }
  await expect(page.locator(".work-operation")).toHaveCount(0);
  const current = page.locator(".case-detail");
  // This status is hidden during revalidation and returns only with current disclosure.
  await expect(
    current.getByText(family === "WORK" ? "Case work recorded." : "Reminder operation recorded.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(current.locator(".case-work")).toBeVisible();
  await expect(current.locator(".case-reminders")).toBeVisible();
  await expectCaseReadsSettled(page);
  return payload.outcome.data;
};
export const workRequest = async <K extends keyof RequestByEndpoint>(
  page: Page,
  endpoint: K,
  body: RequestByEndpoint[K],
) => {
  const token = await sessionToken(page);
  const entry = endpoints.find((value) => value.id === endpoint)!;
  const result = await browserRequest(page, entry.path, {
    method: entry.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
      ...(entry.requiresInstallation
        ? { "ClaimCore-Installation": await installationId(page, token) }
        : {}),
    },
    body: JSON.stringify(body),
  });
  if (result.status !== 200 || !isResponse(endpoint, result.payload)) {
    const detail = isHostFailure(result.payload, result.status)
      ? result.payload.hostFailure.code
      : "INVALID_RESPONSE";
    throw new Error(`E2E_WORK_READBACK_HTTP_${result.status}_${detail}`);
  }
  return result.payload;
};
