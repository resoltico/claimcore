import { readFile } from "node:fs/promises";
import { type Page } from "@playwright/test";

import { isResponse } from "../src/generated/contracts/validation";
import { endpoints } from "../src/generated/contracts/endpoint-catalog";
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

type ManagementEndpoint =
  "authority.register" | "authority.setGrant" | "authority.setEnabled" | "authority.observe";

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const pathFor = (endpoint: ManagementEndpoint): string => {
  const descriptor = endpoints.find((item) => item.id === endpoint);
  if (descriptor === undefined) {
    throw new Error("Synthetic authority endpoint is missing.");
  }
  return descriptor.path;
};

const call = async (page: Page, endpoint: ManagementEndpoint, body: object) => {
  const token = await sessionToken(page);
  const reply = await browserRequest(page, pathFor(endpoint), {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
    },
    body: JSON.stringify(body),
  });
  const { payload } = reply;
  if (reply.status !== 200 || !(await isResponse(endpoint, payload))) {
    await progress(`authority-wire-${endpoint.replace(".", "-")}-${reply.status}`);
    throw new Error(`Synthetic authority ${endpoint} wire response is invalid.`);
  }
  if (
    !isObject(payload) ||
    !isObject(payload["outcome"]) ||
    typeof payload["outcome"]["tag"] !== "string"
  ) {
    await progress(`authority-outcome-malformed-${endpoint.replace(".", "-")}`);
    throw new Error("Synthetic authority outcome is malformed.");
  }
  await progress(
    `authority-${endpoint.replace(".", "-")}-${payload["outcome"]["tag"].toLowerCase()}`,
  );
  return payload["outcome"]["tag"];
};

export const applyAuthority = async (
  page: Page,
  endpoint: Exclude<ManagementEndpoint, "authority.observe">,
  body: Readonly<Record<string, unknown>> & { eventId: string },
) => {
  const tag = await call(page, endpoint, body);
  if (tag === "APPLIED" || tag === "ALREADY_APPLIED") {
    return;
  }
  if (tag !== "OUTCOME_UNKNOWN") {
    throw new Error("Synthetic authority action was refused.");
  }
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const observed = await call(page, "authority.observe", { eventId: body.eventId });
    if (observed === "APPLIED") {
      return;
    }
    if (observed !== "OUTCOME_UNKNOWN") {
      break;
    }
  }
  throw new Error("Synthetic authority completion remains unconfirmed.");
};
