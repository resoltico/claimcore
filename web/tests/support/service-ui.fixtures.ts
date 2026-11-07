import { actorContextInput, positiveValue } from "./inputs";
import { transportResponse } from "./transport-response.fixtures";
import { consumerContractIdentity } from "@contracts/client";
export { productMetadata as definition } from "../../src/protocol/productMetadata";
export { caseFields as fields, operationId } from "./service-foundation.fixtures";
export const runtime = {
  productVersion: "0.8.0",
  observedAt: "2026-09-09T12:00:00.000000Z",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
const fixtureData = (endpoint: string, kind: string, data: unknown): unknown => {
  if (kind !== "SUCCEEDED") {
    return data;
  }
  if (endpoint === "context") {
    return { requester: data };
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
  // Editor observations carry the wire view locally; it is a sibling of data in HTTP replies.
  if (endpoint === "case.get" && typeof data === "object" && data !== null && "view" in data) {
    const { view: _view, ...facts } = data;
    return positiveValue(facts);
  }
  return positiveValue(data);
};
export const response = (endpoint: string, kind: string, data: unknown, status = 200): Response =>
  transportResponse(endpoint, kind, fixtureData(endpoint, kind, data), status);

export const context = actorContextInput({
  ...runtime,
  capabilities: ["REGISTER"],
  views: ["CASES", "AUTHORITY", "OPERATIONS"],
  offers: [{ kind: "OPEN" }],
});

export const unavailable = {
  code: "RESOURCE_UNAVAILABLE",
  action: "ASK_FOR_ACCESS",
} as const;
