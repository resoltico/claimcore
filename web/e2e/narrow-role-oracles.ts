import { expect, type Page } from "@playwright/test";
import { endpoints } from "@contracts/client";
import { browserRequest, sessionToken } from "./session-helpers";
import { randomUUID } from "node:crypto";
import { isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/protocol/types";

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
  expect(isResponse(endpoint, payload)).toBe(true);
  const { outcome } = payload as ServiceResponse<typeof endpoint>;
  if (outcome.kind !== "REJECTED") {
    throw new Error("Narrow read did not return its actual authorization refusal.");
  }
  expect(outcome.data.code).toBe("RESOURCE_UNAVAILABLE");
};

const directCaseRefusal = async (page: Page, reference: string) => {
  const endpoint = endpoints.find((entry) => entry.id === "case.get")!;
  const reply = await browserRequest(page, endpoint.path, {
    method: endpoint.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference: reference }),
  });
  expect(reply.status).toBe(200);
  expect(reply.cacheControl).toContain("no-store");
  if (!isResponse("case.get", reply.payload) || reply.payload.outcome.kind !== "REJECTED") {
    throw new Error("A case without read authority must return a typed public refusal.");
  }
  expect(reply.payload.outcome.data.code).toBe("RESOURCE_UNAVAILABLE");
  return reply.payload.outcome;
};

export const opaqueCaseRefusal = async (page: Page, reference: string) => {
  const actual = await directCaseRefusal(page, reference);
  const missing = await directCaseRefusal(page, `MISSING-${randomUUID()}`);
  expect(actual).toEqual(missing);
};

export const noCaseWorkspace = async (page: Page) => {
  await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Cases", exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Open by exact reference", exact: true }),
  ).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Current case", exact: true })).toHaveCount(0);
};
