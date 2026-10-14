import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import Ajv2020, { type AnySchema, type ValidateFunction } from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { endpoints, type EndpointId } from "../src/generated/contracts/endpoint-catalog";
import type { ServiceResponse } from "../src/api/types";
const directory = resolve(import.meta.dirname, "../src/generated/contracts");
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
export const endpointInventory = endpoints.map((e) => e.id);
export const requiredWebEndpoint = (name: string): EndpointId => {
  const found = endpoints.find((e) => e.id === name);
  if (found === undefined) {
    throw new Error("Missing endpoint.");
  }
  return found.id;
};
export const corpusCases = (): CorpusCase[] => {
  const raw = readUnknown("endpoint-corpus.json");
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
  if (
    value.endpoint === "case.history" &&
    value.outcome.tag === "SUCCEEDED" &&
    value.outcome.data.tag === "FOUND"
  ) {
    const accepted = generatedWebValue("command.execute");
    if (accepted.outcome.tag !== "ACCEPTED") {
      throw new Error("Receipt fixture absent.");
    }
    return {
      ...value,
      outcome: {
        ...value.outcome,
        data: {
          ...value.outcome.data,
          entries: [
            {
              tag: "SUMMARY",
              change: {
                operationId: accepted.outcome.data.operationId,
                position: accepted.outcome.data.position,
                revision: accepted.outcome.data.revision,
                act: accepted.outcome.data.act,
                recordedAt: accepted.outcome.data.recordedAt,
                recordedBy: accepted.outcome.data.recordedBy,
              },
            },
            { tag: "FULL", receipt: accepted.outcome.data },
          ],
        },
      },
    };
  }
  return value;
};
export const generatedResponse = (endpoint: EndpointId, status = 200): Response =>
  new Response(JSON.stringify(generatedWebValue(endpoint)), {
    status,
    headers: { "content-type": "application/json" },
  });
export const schemaValidator = (file: string): ValidateFunction => {
  const compiler = new Ajv2020({
    strict: true,
    coerceTypes: false,
    removeAdditional: false,
    useDefaults: false,
  });
  addFormats(compiler);
  return compiler.compile(readUnknown(file) as AnySchema);
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
