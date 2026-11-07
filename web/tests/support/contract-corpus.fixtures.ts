import { transportResponse } from "./transport-response.fixtures";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { EndpointId } from "@contracts/client";
import type { ServiceResponse } from "../../src/protocol/types";
import { isResponse } from "../../src/protocol/client";
const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export type CorpusCase = {
  id: string;
  endpoint: EndpointId;
  direction: "request" | "response";
  valid: boolean;
  value: unknown;
};
/** A fixture identity must select exactly one authored value within its wire direction. */
export const assertUniqueCorpusIdentities = (cases: ReadonlyArray<CorpusCase>): void => {
  const identities = new Set<string>();
  for (const sample of cases) {
    const identity = `${sample.direction}:${sample.id}`;
    if (identities.has(identity)) {
      throw new Error(`Duplicate authored corpus identity: ${identity}`);
    }
    identities.add(identity);
  }
};
export const corpusCases = (): CorpusCase[] => {
  const cases = (["request", "response"] as const).flatMap((direction) => {
    const raw: unknown = JSON.parse(
      readFileSync(
        resolve(import.meta.dirname, `../../../tests/fixtures/examples/${direction}s.json`),
        "utf8",
      ),
    );
    if (!isObject(raw) || !Array.isArray(raw["cases"])) {
      throw new Error("Authored examples absent.");
    }
    return raw["cases"].map((row: Record<string, unknown>) => ({
      id: String(row["id"] ?? row["name"]),
      endpoint: String(row["endpoint"]) as EndpointId,
      direction,
      valid: row["valid"] === true,
      value: row["value"],
    }));
  });
  assertUniqueCorpusIdentities(cases);
  return cases;
};

const hasScope = (value: unknown, endpoint: EndpointId, subject: string): boolean => {
  if (!isObject(value) || !isObject(value["outcome"]) || !isObject(value["outcome"]["data"])) {
    return false;
  }
  const { data } = value["outcome"];
  if (endpoint === "operation.preview") {
    return (
      isObject(data["review"]) &&
      isObject(data["review"]["subject"]) &&
      data["review"]["subject"]["kind"] === subject
    );
  }
  return isObject(data["subject"]) && data["subject"]["kind"] === subject;
};
export const wireReply = <K extends EndpointId>(
  endpoint: K,
  subject: "CASE" | "AUTHORITY" = "CASE",
): ServiceResponse<K> => {
  const sample = corpusCases().find(
    (row) =>
      row.endpoint === endpoint &&
      row.direction === "response" &&
      row.valid &&
      isObject(row.value) &&
      isObject(row.value["outcome"]) &&
      ["SUCCEEDED", "ACCEPTED", "ALREADY_ACCEPTED", "REVIEWED", "NOT_ACCEPTED"].includes(
        String(row.value["outcome"]["kind"]),
      ) &&
      (!["operation.execute", "operation.preview"].includes(endpoint) ||
        hasScope(row.value, endpoint, subject)),
  );
  if (sample === undefined || !isResponse(endpoint, sample.value)) {
    throw new Error("Admitted synthetic reply absent.");
  }
  return structuredClone(sample.value);
};
export const wireResponse = (
  endpoint: EndpointId,
  status = 200,
  subject: "CASE" | "AUTHORITY" = "CASE",
): Response => {
  const reply = wireReply(endpoint, subject);
  return transportResponse(endpoint, reply.outcome.kind, reply.outcome.data, status);
};
export const hostCases = () => [
  {
    id: "input-invalid",
    valid: true,
    endpoint: null,
    status: 400,
    value: { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" } },
  },
];
