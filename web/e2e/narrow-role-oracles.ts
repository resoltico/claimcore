import { type ResponseFor, endpoints } from "@contracts/client";
import { showAccountActions } from "./workspace-navigation";
import { expect, type Page } from "@playwright/test";
import { browserRequest, sessionToken } from "./session-helpers";
import { randomUUID } from "node:crypto";
export const refusedRead = async (
  page: Page,
  endpoint: "case.get",
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
  const endpointReply = (await response.json()) as ResponseFor<typeof endpoint, "HUMAN">;
  if (!(!("representation" in endpointReply) || endpointReply.representation === "HUMAN")) {
    throw new Error("Expected an admitted HUMAN refusal.");
  }
  const { outcome } = endpointReply;
  if (outcome.kind !== "REJECTED") {
    throw new Error("Narrow read did not return its actual authorization refusal.");
  }
  expect(outcome.rejection.kind).toBe("REFUSED");
  expect(outcome.rejection.advice.message.messageId).toBe("diagnostic.RESOURCE_UNAVAILABLE");
};
export const metadataTimeline = async (page: Page, caseReference: string) => {
  const reply = await browserRequest(page, "/api/case.timeline", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      target: {
        kind: "REFERENCE",
        caseReference,
      },
      filter: "FACTS",
      limit: 50,
    }),
  });
  expect(reply.status).toBe(200);
  const caseTimelineReply = reply.payload as ResponseFor<"case.timeline", "DATA">;
  if (
    caseTimelineReply.representation !== "DATA" ||
    caseTimelineReply.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("A metadata reader must receive an admitted timeline.");
  }
  expect(caseTimelineReply.outcome.data.items.length).toBeGreaterThan(0);
  for (const entry of caseTimelineReply.outcome.data.items) {
    expect(entry.changes).toEqual({
      kind: "WITHHELD",
    });
  }
};
const directCaseRefusal = async (page: Page, reference: string) => {
  const endpoint = endpoints.find((entry) => entry.id === "case.get")!;
  const reply = await browserRequest(page, endpoint.path, {
    method: endpoint.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
    }),
  });
  expect(reply.status).toBe(200);
  expect(reply.cacheControl).toContain("no-store");
  const caseGetReply = reply.payload as ResponseFor<"case.get", "DATA">;
  if (caseGetReply.representation !== "DATA" || caseGetReply.outcome.kind !== "REJECTED") {
    throw new Error("A case without read people must return a typed public refusal.");
  }
  expect(caseGetReply.outcome.rejection.code).toBe("RESOURCE_UNAVAILABLE");
  return caseGetReply.outcome;
};
export const opaqueCaseRefusal = async (page: Page, reference: string) => {
  const actual = await directCaseRefusal(page, reference);
  const missing = await directCaseRefusal(page, `MISSING-${randomUUID()}`);
  expect(actual).toEqual(missing);
};
export const noCaseWorkspace = async (page: Page) => {
  const signOut = page.getByRole("button", {
    name: "Sign out",
    exact: true,
  });
  if (!(await signOut.isVisible())) {
    await showAccountActions(page);
  }
  await expect(signOut).toBeVisible();
  await expect(
    page.getByRole("link", {
      name: "Cases",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("heading", {
      name: "Cases",
      exact: true,
      level: 1,
    }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("link", {
      name: "Open a case",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("button", {
      name: "Open by exact reference",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(page.locator("section.case-detail")).toHaveCount(0);
};
