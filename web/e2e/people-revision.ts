import type { Page } from "@playwright/test";
import { endpoints, type ResponseFor } from "@contracts/client";
import { browserRequest, sessionToken } from "./session-helpers";
/** One explicit review for a separate machine client, with no conflict retry. */
export const reviewedPeopleBinding = async (page: Page, actorId: string) => {
  const token = await sessionToken(page);
  const endpoint = endpoints.find((entry) => entry.id === "people.grants")!;
  const reply = await browserRequest(page, endpoint.path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
    body: JSON.stringify({ actorId, limit: 1 }),
  });
  const payload = reply.payload as ResponseFor<"people.grants", "DATA">;
  if (reply.status !== 200 || payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected a reviewed account");
  }
  return { kind: "PEOPLE" as const, actorId, expectedRevision: payload.outcome.data.revision };
};
