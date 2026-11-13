import { type ResponseFor, type RequestByEndpoint, endpoints } from "@contracts/client";
import { readFile } from "node:fs/promises";
import { type Page } from "@playwright/test";
import { browserRequest, progress, sessionToken, installationId } from "./session-helpers";
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
export const applyPeople = async (page: Page, body: RequestByEndpoint["operation.execute"]) => {
  const endpoint = endpoints.find((entry) => entry.id === "operation.execute");
  if (endpoint === undefined) {
    throw new Error("Synthetic people endpoint is missing.");
  }
  const bytes = JSON.stringify(body);
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const antiforgery = await sessionToken(page);
    const reply = await browserRequest(page, endpoint.path, {
      method: endpoint.method,
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": antiforgery,
        "ClaimCore-Installation": await installationId(page, antiforgery),
      },
      body: bytes,
    });
    const operationExecuteReply = reply.payload as ResponseFor<"operation.execute", "DATA">;
    if (reply.status !== 200 || operationExecuteReply.representation !== "DATA") {
      throw new Error("Synthetic people response is invalid.");
    }
    const { outcome } = operationExecuteReply;
    await progress(`people-execute-${outcome.kind.toLowerCase()}`);
    if (outcome.kind === "ACCEPTED" || outcome.kind === "ALREADY_ACCEPTED") {
      return;
    }
    if (outcome.kind !== "OUTCOME_UNKNOWN") {
      const code = outcome.kind === "REJECTED" ? outcome.rejection.code : outcome.fault.code;
      throw new Error(`Synthetic people action ${outcome.kind}: ${code}`);
    }
  }
  throw new Error("Synthetic people completion remains unconfirmed; original request retained.");
};
