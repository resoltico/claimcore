import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, type Page } from "@playwright/test";

import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import { webV3Endpoints } from "../src/generated/contracts/web-v3.endpoint-catalog";
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
  const descriptor = webV3Endpoints.find((item) => item.id === endpoint);
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
  if (reply.status !== 200 || !(await isWebV3Response(endpoint, payload))) {
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
  if (tag === "APPLIED") {
    return;
  }
  if (tag !== "UNCONFIRMED") {
    throw new Error("Synthetic authority action was refused.");
  }
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const observed = await call(page, "authority.observe", { eventId: body.eventId });
    if (observed === "APPLIED") {
      return;
    }
    if (observed !== "UNCONFIRMED") {
      break;
    }
  }
  throw new Error("Synthetic authority completion remains unconfirmed.");
};

const grantService = async (page: Page, principal: object, scope: object): Promise<void> => {
  await progress("authority-service-register-start");
  await applyAuthority(page, "authority.register", { eventId: randomUUID(), principal });
  await progress("authority-service-register-done");
  for (const role of ["CASE_EDITOR", "RECOVERY_OPERATOR", "RECOVERY_EXPORTER"] as const) {
    await progress(`authority-service-${role.toLowerCase().replaceAll("_", "-")}-start`);
    await applyAuthority(page, "authority.setGrant", {
      eventId: randomUUID(),
      principal,
      role,
      scope,
      active: true,
    });
    await progress(`authority-service-${role.toLowerCase().replaceAll("_", "-")}-done`);
  }
};

const assertSession = async (page: Page): Promise<void> => {
  const snapshot = await browserRequest(page, "/api/v3/session");
  expect(snapshot.status).toBe(200);
};

export const grantSyntheticCasework = async (page: Page): Promise<void> => {
  await progress("authority-principals-start");
  let inventory: Principals;
  try {
    inventory = await syntheticPrincipals();
  } catch {
    await progress("authority-principals-invalid");
    throw new Error("Synthetic principal inventory admission failed.");
  }
  await progress("authority-principals-ready");
  const { issuer, ownerSubject, stewardSubject, serviceClientId } = inventory;
  const owner = { kind: "HUMAN", issuer, subject: ownerSubject };
  const steward = { kind: "HUMAN", issuer, subject: stewardSubject };
  const service = { kind: "SERVICE", issuer, clientId: serviceClientId };
  const scope = { kind: "INSTALLATION" };
  await progress("authority-register-start");
  await applyAuthority(page, "authority.register", { eventId: randomUUID(), principal: steward });
  await progress("authority-register-done");
  await progress("authority-steward-grant-start");
  await applyAuthority(page, "authority.setGrant", {
    eventId: randomUUID(),
    principal: steward,
    role: "DATA_STEWARD",
    scope,
    active: true,
  });
  await progress("authority-steward-grant-done");
  await progress("authority-owner-editor-start");
  await applyAuthority(page, "authority.setGrant", {
    eventId: randomUUID(),
    principal: owner,
    role: "CASE_EDITOR",
    scope,
    active: true,
  });
  await progress("authority-owner-editor-done");
  for (const role of ["RECOVERY_OPERATOR", "RECOVERY_EXPORTER"] as const) {
    await progress(`authority-owner-${role.toLowerCase().replace("_", "-")}-start`);
    await applyAuthority(page, "authority.setGrant", {
      eventId: randomUUID(),
      principal: owner,
      role,
      scope,
      active: true,
    });
    await progress(`authority-owner-${role.toLowerCase().replace("_", "-")}-done`);
  }
  await grantService(page, service, scope);
  await assertSession(page);
};
