import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, type Download, type Page } from "@playwright/test";
import { webV3Endpoints } from "../src/generated/contracts/web-v3.endpoint-catalog";
import { browserRequest, sessionToken } from "./session-helpers";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import type { RoleFixture } from "./narrow-role-fixture";

export const verifyClosed = async (owner: Page, fixture: RoleFixture) => {
  const headers = {
    "Content-Type": "application/json",
    "X-ClaimCore-Antiforgery": await sessionToken(owner),
  };
  const current = await browserRequest(owner, "/api/v3/cases/get", {
    method: "POST",
    headers,
    body: JSON.stringify({ caseReference: fixture.reference }),
  });
  expect(current.status).toBe(200);
  expect(await isWebV3Response("case.get", current.payload)).toBe(true);
  const result = (current.payload as WebV3Response<"case.get">).outcome;
  if (result.tag !== "SUCCEEDED" || result.data.tag !== "FOUND") {
    throw new Error("Independent owner current read is unavailable.");
  }
  expect(result.data.current.case.revision).toBe("2");
  expect(result.data.current.case.fields.status).toBe("CLOSED");
  const history = await browserRequest(owner, "/api/v3/cases/history", {
    method: "POST",
    headers,
    body: JSON.stringify({ caseReference: fixture.reference, detail: "SUMMARY", limit: 50 }),
  });
  expect(history.status).toBe(200);
  expect(await isWebV3Response("case.history", history.payload)).toBe(true);
  const accepted = (history.payload as WebV3Response<"case.history">).outcome;
  if (accepted.tag !== "SUCCEEDED" || accepted.data.tag !== "FOUND") {
    throw new Error("Independent owner accepted history is unavailable.");
  }
  expect(accepted.data.entries).toHaveLength(2);
};

export const verifyExport = async (owner: Page, download: Download, fixture: RoleFixture) => {
  const path = await download.path();
  if (path === null) {
    throw new Error("Private exported file is unavailable.");
  }
  const bytes = await readFile(path);
  const artifact: unknown = JSON.parse(bytes.toString("utf8"));
  if (typeof artifact !== "object" || artifact === null || !("operationId" in artifact)) {
    throw new Error("Exported synthetic artifact is malformed.");
  }
  expect(artifact.operationId === fixture.identity.operationId).toBe(true);
  const reply = await browserRequest(owner, "/api/v3/recovery/import-envelope/preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/vnd.claimcore.recovery+json",
      "X-ClaimCore-Antiforgery": await sessionToken(owner),
      "X-ClaimCore-Source-Sha256": createHash("sha256").update(bytes).digest("hex"),
    },
    body: bytes.toString("utf8"),
  });
  expect(reply.status).toBe(200);
  expect(await isWebV3Response("recovery.importEnvelopePreview", reply.payload)).toBe(true);
  const preview = (reply.payload as WebV3Response<"recovery.importEnvelopePreview">).outcome;
  if (preview.tag !== "SUCCEEDED") {
    throw new Error("Independent owner artifact preview is unavailable.");
  }
  expect(preview.data.decodedEffect.operationId).toBe(fixture.identity.operationId);
  expect(preview.data.decodedEffect.requestSha256).toBe(fixture.identity.requestSha256);
};

export const acceptedResolution = async (page: Page, dispatch: () => Promise<void>) => {
  const waiting = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v3/recovery/resolve",
  );
  await dispatch();
  const response = await waiting;
  expect(response.status()).toBe(200);
  const payload: unknown = await response.json();
  expect(await isWebV3Response("recovery.resolve", payload)).toBe(true);
  const { outcome } = payload as WebV3Response<"recovery.resolve">;
  if (outcome.tag === "COMPLETED") {
    expect(outcome.data.execution.tag).toBe("ACCEPTED");
    expect(outcome.data.settlement).toBe("CONFIRMED");
  } else {
    expect(outcome.tag).toBe("OBSERVED_ACCEPTED");
  }
};

export const refusedRead = async (
  page: Page,
  endpoint: "case.get" | "case.history",
  dispatch: () => Promise<void>,
) => {
  const path = webV3Endpoints.find((entry) => entry.id === endpoint)?.path;
  if (path === undefined) {
    throw new Error("Generated read endpoint is missing.");
  }
  const waiting = page.waitForResponse((response) => new URL(response.url()).pathname === path);
  await dispatch();
  const response = await waiting;
  expect(response.status()).toBe(200);
  const payload: unknown = await response.json();
  expect(await isWebV3Response(endpoint, payload)).toBe(true);
  const { outcome } = payload as WebV3Response<typeof endpoint>;
  if (outcome.tag !== "REJECTED") {
    throw new Error("Narrow read did not return its actual authorization refusal.");
  }
  expect(outcome.data.code).toBe("RESOURCE_UNAVAILABLE");
};
