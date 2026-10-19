import { expect, type Page } from "@playwright/test";
import { endpoints } from "@contracts/client";
import { isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/api/types";

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
  if (outcome.tag !== "REJECTED") {
    throw new Error("Narrow read did not return its actual authorization refusal.");
  }
  expect(outcome.data.diagnostic.id).toBe("ACCESS_RESOURCE_UNAVAILABLE");
};
