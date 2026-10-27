import { transportResponse } from "./transport-response.fixtures";
import { consumerContractIdentity } from "@contracts/client";
export { productMetadata as definition } from "../src/protocol/productMetadata";
export { caseFields as fields, operationId } from "./service-foundation.fixtures";
export const runtime = {
  productVersion: "0.8.0",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
const fixtureData = (endpoint: string, kind: string, data: unknown): unknown => {
  if (kind !== "SUCCEEDED") {
    return data;
  }
  if (endpoint === "session.logout") {
    return null;
  }
  if (endpoint === "session") {
    return {
      contractIdentity: consumerContractIdentity,
      ...(typeof data === "object" && data !== null ? data : {}),
    };
  }
  return data;
};
export const response = (endpoint: string, kind: string, data: unknown, status = 200): Response =>
  transportResponse(endpoint, kind, fixtureData(endpoint, kind, data), status);

export const context = {
  ...runtime,
  capabilities: ["REGISTER"],
  views: ["CASES", "AUTHORITY", "OPERATIONS"],
  offers: [{ kind: "OPEN", expectedRevision: 0 }],
};

export const unavailable = {
  code: "RESOURCE_UNAVAILABLE",
  action: "NONE_REQUIRED",
  target: null,
  actualRevision: null,
  limits: {},
} as const;
