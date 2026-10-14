import { expect, type Page } from "@playwright/test";
import { endpoints } from "../src/generated/contracts/endpoint-catalog";
import { browserRequest, sessionToken } from "./session-helpers";
import { isResponse } from "../src/generated/contracts/validation";
import type { ServiceResponse } from "../src/api/types";
import type { RoleFixture } from "./narrow-role-fixture";

export const verifyClosed = async (owner: Page, fixture: RoleFixture) => {
  const headers = {
    "Content-Type": "application/json",
    "X-ClaimCore-Antiforgery": await sessionToken(owner),
  };
  const current = await browserRequest(owner, "/api/case.get", {
    method: "POST",
    headers,
    body: JSON.stringify({ caseReference: fixture.reference }),
  });
  expect(current.status).toBe(200);
  expect(await isResponse("case.get", current.payload)).toBe(true);
  const result = (current.payload as ServiceResponse<"case.get">).outcome;
  if (
    result.tag !== "SUCCEEDED" ||
    result.data.tag !== "FOUND" ||
    result.data.current.state !== "LIVE"
  ) {
    throw new Error("Independent owner current read is unavailable.");
  }
  expect(result.data.current.case.revision).toBe("2");
  expect(result.data.current.case.fields.status).toBe("CLOSED");
  const history = await browserRequest(owner, "/api/case.history", {
    method: "POST",
    headers,
    body: JSON.stringify({ caseReference: fixture.reference, detail: "SUMMARY", limit: 50 }),
  });
  expect(history.status).toBe(200);
  expect(await isResponse("case.history", history.payload)).toBe(true);
  const accepted = (history.payload as ServiceResponse<"case.history">).outcome;
  if (accepted.tag !== "SUCCEEDED" || accepted.data.tag !== "FOUND") {
    throw new Error("Independent owner accepted history is unavailable.");
  }
  expect(accepted.data.entries).toHaveLength(2);
};

export const refusedRead = async (
  page: Page,
  endpoint: "case.get" | "case.history",
  dispatch: () => Promise<void>,
) => {
  const path = endpoints.find((entry) => entry.id === endpoint)?.path;
  if (path === undefined) {
    throw new Error("Generated read endpoint is missing.");
  }
  const waiting = page.waitForResponse((response) => new URL(response.url()).pathname === path);
  await dispatch();
  const response = await waiting;
  expect(response.status()).toBe(200);
  const payload: unknown = await response.json();
  expect(await isResponse(endpoint, payload)).toBe(true);
  const { outcome } = payload as ServiceResponse<typeof endpoint>;
  if (outcome.tag !== "REJECTED") {
    throw new Error("Narrow read did not return its actual authorization refusal.");
  }
  expect(outcome.data.code).toBe("RESOURCE_UNAVAILABLE");
};
