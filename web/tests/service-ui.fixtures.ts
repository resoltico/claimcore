import { wireContractFingerprint } from "@contracts/endpoint-catalog";
export { productMetadata as definition } from "@contracts/product-metadata";
export { caseFields as fields, operationId, digest, review } from "./service-foundation.fixtures";
export const runtime = {
  productVersion: "0.8.0",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
const fixtureData = (endpoint: string, data: unknown): unknown => {
  if (endpoint === "session.logout") {
    return null;
  }
  if (endpoint === "session") {
    return {
      wireFingerprint: wireContractFingerprint,
      ...(typeof data === "object" && data !== null ? data : {}),
    };
  }
  return data;
};
export const response = (endpoint: string, tag: string, data: unknown, status = 200): Response =>
  new Response(JSON.stringify({ endpoint, outcome: { tag, data: fixtureData(endpoint, data) } }), {
    status,
    headers: { "content-type": "application/json" },
  });

export const context = { ...runtime, capabilities: ["REGISTER"] };

export const unavailable = {
  diagnostic: { id: "ACCESS_RESOURCE_UNAVAILABLE", parameters: {} },
  message: "Synthetic unavailable resource",
  field: null,
  actualRevision: null,
  recommendedAction: "NONE_REQUIRED",
} as const;
