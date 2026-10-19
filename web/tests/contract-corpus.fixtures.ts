import { transportResponse } from "./transport-response.fixtures";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { EndpointId } from "@contracts/client";
import type { ServiceResponse } from "../src/api/types";
const directory = resolve(import.meta.dirname, "../../contracts");
export const readUnknown = (file: string): unknown =>
  JSON.parse(readFileSync(resolve(directory, file), "utf8"));
export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export type CorpusCase = {
  id: string;
  endpoint: EndpointId;
  direction: "request" | "response";
  valid: boolean;
  value: unknown;
};
export const corpusCases = (): CorpusCase[] => {
  const raw = readUnknown("corpus.json");
  if (!isObject(raw) || !Array.isArray(raw["cases"])) {
    throw new Error("Corpus absent.");
  }
  return raw["cases"] as CorpusCase[];
};
export const generatedWebValue = <K extends EndpointId>(endpoint: K): ServiceResponse<K> => {
  const sample = corpusCases().find(
    (c) => c.endpoint === endpoint && c.direction === "response" && c.valid,
  );
  if (sample === undefined) {
    throw new Error("Positive response absent.");
  }
  const value = structuredClone(sample.value) as ServiceResponse<K>;
  if (value.endpoint === "case.history" && value.outcome.tag === "SUCCEEDED") {
    const accepted = generatedWebValue("case.execute");
    if (accepted.outcome.tag !== "ACCEPTED") {
      throw new Error("CaseReceipt fixture absent.");
    }
    return {
      ...value,
      outcome: {
        ...value.outcome,
        data: {
          ...value.outcome.data,
          entries: [{ tag: "FULL", receipt: accepted.outcome.data }],
        },
      },
    };
  }
  return value;
};
export const generatedResponse = (endpoint: EndpointId, status = 200): Response => {
  const response = generatedWebValue(endpoint);
  return transportResponse(endpoint, response.outcome.tag, response.outcome.data, status);
};
export const hostCases = () => [
  {
    id: "input-invalid",
    valid: true,
    endpoint: null,
    status: 400,
    value: {
      kind: "HOST_FAILURE",
      code: "INPUT_INVALID",
      status: 400,
      message: "The input does not satisfy the endpoint contract.",
      diagnostic: { id: "HOST_INPUTINVALID", parameters: {} },
      executionPhase: "NOT_STARTED",
    },
  },
];

export const historyVariants = () => {
  const accepted = generatedWebValue("case.execute").outcome;
  if (accepted.tag !== "ACCEPTED") {
    throw new Error("Receipt fixture absent.");
  }
  const receipt = accepted.data;
  const { operationId, position, revision, act, recordedAt, recordedBy } = receipt;
  return {
    full: { tag: "FULL" as const, receipt },
    summary: {
      tag: "SUMMARY" as const,
      change: { operationId, position, revision, act, recordedAt, recordedBy },
    },
  };
};
