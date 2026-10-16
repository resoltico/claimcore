import type { RequestByEndpoint } from "@contracts/types";
import { readFile } from "node:fs/promises";
import { type Page } from "@playwright/test";

import { isResponse } from "@contracts/browser-contract";
import { endpoints } from "@contracts/endpoint-catalog";
import { browserRequest, progress, sessionToken } from "./session-helpers";

type Principals = Readonly<{
  issuer: string;
  ownerSubject: string;
  stewardSubject: string;
  serviceClientId: string;
}>;

const validPrincipals = (value: Record<string, unknown>): value is Principals => {
  const { issuer, ownerSubject, stewardSubject, serviceClientId } = value;
  return (
    typeof issuer === "string" &&
    issuer.startsWith("https://") &&
    typeof ownerSubject === "string" &&
    ownerSubject.length > 0 &&
    typeof stewardSubject === "string" &&
    stewardSubject.length > 0 &&
    typeof serviceClientId === "string" &&
    serviceClientId.length > 0 &&
    ownerSubject !== stewardSubject
  );
};

export const syntheticPrincipals = async (): Promise<Principals> => {
  const file = process.env["CLAIMCORE_WEB_E2E_PRINCIPALS_FILE"];
  if (file === undefined) {
    throw new Error("Synthetic principal inventory is missing.");
  }
  const source: unknown = JSON.parse(await readFile(file, "utf8"));
  if (typeof source !== "object" || source === null) {
    throw new Error("Synthetic principal inventory is invalid.");
  }
  if (!validPrincipals(source as Record<string, unknown>)) {
    throw new Error("Synthetic principal inventory is invalid.");
  }
  return source as Principals;
};

export const applyAuthority = async (page: Page, body: RequestByEndpoint["authority.execute"]) => {
  const endpoint = endpoints.find((entry) => entry.id === "authority.execute");
  if (endpoint === undefined) {
    throw new Error("Synthetic authority endpoint is missing.");
  }
  const bytes = JSON.stringify(body);
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const reply = await browserRequest(page, endpoint.path, {
      method: endpoint.method,
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: bytes,
    });
    if (reply.status !== 200 || !isResponse("authority.execute", reply.payload)) {
      throw new Error("Synthetic authority response is invalid.");
    }
    const { outcome } = reply.payload;
    await progress(`authority-execute-${outcome.tag.toLowerCase()}`);
    if (outcome.tag === "ACCEPTED" || outcome.tag === "ALREADY_ACCEPTED") {
      return;
    }
    if (outcome.tag !== "OUTCOME_UNKNOWN") {
      throw new Error("Synthetic authority action was refused.");
    }
  }
  throw new Error("Synthetic authority completion remains unconfirmed; original request retained.");
};
