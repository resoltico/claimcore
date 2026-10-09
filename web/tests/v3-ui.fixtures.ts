import { webV3WireContractFingerprint } from "../src/generated/contracts/web-v3.endpoint-catalog";
import type { CaseFields, PreparationDetails } from "../src/api/v3";
import { generatedWebValue } from "./contract-corpus.fixtures";

export { productMetadata as definition } from "../src/generated/contracts/web-v3.product-metadata";

const foundCase = generatedWebValue("case.get");
if (foundCase.outcome.tag !== "SUCCEEDED" || foundCase.outcome.data.tag !== "FOUND") {
  throw new Error("Generated case fixture must be found.");
}

export const fields: CaseFields = {
  ...foundCase.outcome.data.current.case.fields,
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "Latvia",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  caseReference: "CASE-1",
  paymentDecisionDate: null,
  payableAmount: null,
  payableCurrency: null,
  paymentDate: null,
  status: "OPENED",
};

const prepared = generatedWebValue("command.prepare");
if (prepared.outcome.tag !== "PREPARED") {
  throw new Error("Generated preparation fixture must be prepared.");
}

export const { operationId } = prepared.outcome.data.details.summary;
export const runtime = prepared.outcome.data.review.context;

export const preparation: PreparationDetails = {
  ...prepared.outcome.data.details,
  summary: {
    ...prepared.outcome.data.details.summary,
    caseReference: fields.caseReference,
    command: "CLOSE",
  },
};

export const recoveryPage = (
  entries: unknown[] = [preparation.summary],
  view: "PENDING" | "TERMINAL" = "PENDING",
  nextCursor: string | null = null,
) => ({
  view,
  items: entries.map((entry) =>
    typeof entry === "object" && entry !== null && "operationId" in entry
      ? { tag: "RETAINED", summary: entry }
      : entry,
  ),
  nextCursor,
  pendingPreparationCount: 1,
  pendingCanonicalRequestBytes: 99,
  maximumPendingPreparations: 1024,
  maximumPendingCanonicalRequestBytes: 64 * 1024 * 1024,
  nearCapacity: false,
});

const fixtureData = (endpoint: string, tag: string, data: unknown): unknown => {
  if (typeof data !== "object" || data === null || Array.isArray(data)) {
    return data;
  }
  if (endpoint === "case.list" && tag === "SUCCEEDED") {
    return { availableCommands: ["OPEN"], ...data };
  }
  if (endpoint.startsWith("session") && tag === "SNAPSHOT") {
    return { webFingerprint: webV3WireContractFingerprint, ...data };
  }
  return data;
};
export const response = (endpoint: string, tag: string, data: unknown, status = 200): Response =>
  new Response(
    JSON.stringify({ endpoint, outcome: { tag, data: fixtureData(endpoint, tag, data) } }),
    {
      status,
      headers: { "content-type": "application/json" },
    },
  );
