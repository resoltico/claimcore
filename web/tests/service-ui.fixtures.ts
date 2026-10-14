import { wireContractFingerprint } from "../src/generated/contracts/endpoint-catalog";
export { productMetadata as definition } from "../src/generated/contracts/product-metadata";
export { caseFields as fields, operationId, digest, review } from "./service-foundation.fixtures";
export const runtime = {
  productVersion: "0.8.0",
  effectiveBusinessDate: "2026-09-09",
  timeZoneId: "Etc/UTC",
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
  if (endpoint === "case.list" && typeof data === "object" && data !== null) {
    return { availableCommands: ["OPEN"], ...data };
  }
  return data;
};
export const response = (endpoint: string, tag: string, data: unknown, status = 200): Response =>
  new Response(JSON.stringify({ endpoint, outcome: { tag, data: fixtureData(endpoint, data) } }), {
    status,
    headers: { "content-type": "application/json" },
  });
