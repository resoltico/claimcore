import { readFileSync } from "node:fs";
import { resolve } from "node:path";

import Ajv2020, { type AnySchema, type ValidateFunction } from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

import {
  isWebV3EndpointId,
  type WebV3EndpointId,
  webV3Endpoints,
} from "../src/generated/contracts/web-v3.endpoint-catalog";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";

type ParsedCase = {
  readonly id: string;
  readonly endpoint: string | null;
  readonly valid: boolean;
  readonly value: unknown;
};

export type CliParsedCase = ParsedCase & { readonly exitCode: number };
export type WebParsedCase = ParsedCase & { readonly status: number };

const generated = resolve(import.meta.dirname, "../src/generated/contracts");

export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

export const readUnknown = (file: string): unknown => {
  const parsed: unknown = JSON.parse(readFileSync(resolve(generated, file), "utf8"));
  return parsed;
};

const requiredText = (value: Record<string, unknown>, name: string): string => {
  const item = value[name];
  if (typeof item !== "string" || item.length === 0) {
    throw new Error(`Contract corpus ${name} must be nonempty text.`);
  }
  return item;
};

const optionalEndpoint = (value: Record<string, unknown>): string | null => {
  const { endpoint } = value;
  if (endpoint === null || typeof endpoint === "string") {
    return endpoint;
  }
  throw new Error("Contract corpus endpoint must be text or null.");
};

const commonCase = (value: unknown): ParsedCase => {
  if (!isObject(value)) {
    throw new Error("Contract corpus cases must be objects.");
  }
  const { valid } = value;
  if (typeof valid !== "boolean") {
    throw new Error("Contract corpus valid flag must be boolean.");
  }
  return {
    id: requiredText(value, "id"),
    endpoint: optionalEndpoint(value),
    valid,
    value: value["value"],
  };
};

const corpusCases = (file: string): ReadonlyArray<unknown> => {
  const document = readUnknown(file);
  if (!isObject(document) || document["schemaVersion"] !== 1 || !Array.isArray(document["cases"])) {
    throw new Error("Contract corpus has an invalid envelope.");
  }
  return document["cases"];
};

const integer = (value: unknown, name: string): number => {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) {
    throw new Error(`Contract corpus ${name} must be a safe integer.`);
  }
  return value;
};

export const cliCases = (): ReadonlyArray<CliParsedCase> =>
  corpusCases("cli-v4.parsed-value-corpus.json").map((item) => {
    const common = commonCase(item);
    if (!isObject(item)) {
      throw new Error("CLI corpus case must remain an object.");
    }
    return Object.assign(common, { exitCode: integer(item["exitCode"], "exitCode") });
  });

export const rawCliInput = (identifier: string): unknown => {
  const item = corpusCases("cli-v4.raw-decoder-corpus.json").find(
    (candidate) => isObject(candidate) && candidate["id"] === identifier,
  );
  if (!isObject(item) || typeof item["bytesBase64"] !== "string") {
    throw new Error(`Missing raw CLI corpus case ${identifier}.`);
  }
  const parsed: unknown = JSON.parse(Buffer.from(item["bytesBase64"], "base64").toString("utf8"));
  if (!isObject(parsed)) {
    throw new Error("Raw CLI corpus invocation must be an object.");
  }
  return parsed["input"];
};

export const webCases = (): ReadonlyArray<WebParsedCase> =>
  corpusCases("web-v3.parsed-value-corpus.json").map((item) => {
    const common = commonCase(item);
    if (!isObject(item)) {
      throw new Error("Web corpus case must remain an object.");
    }
    return Object.assign(common, { status: integer(item["status"], "status") });
  });

const schema = (file: string): AnySchema => {
  const value = readUnknown(file);
  if (!isObject(value) || typeof value["$id"] !== "string") {
    throw new Error("Generated JSON Schema must be an identified object.");
  }
  return value;
};

const compiler = () => {
  const value = new Ajv2020({
    allErrors: false,
    coerceTypes: false,
    ownProperties: true,
    removeAdditional: false,
    strict: true,
    useDefaults: false,
    validateFormats: true,
  });
  addFormats(value);
  return value;
};

export const cliCommandPrepareInputValidator = (): ValidateFunction =>
  compiler().compile(schema("cli-v4.endpoint.command.prepare.schema.json"));

export const cliEndpointInventory = (): ReadonlyArray<string> => {
  const catalog = readUnknown("cli-v4.catalog.json");
  if (!isObject(catalog) || !Array.isArray(catalog["endpoints"])) {
    throw new Error("CLI endpoint catalog is invalid.");
  }
  return catalog["endpoints"].map((item: unknown) => {
    if (!isObject(item) || typeof item["id"] !== "string") {
      throw new Error("CLI endpoint identity is invalid.");
    }
    return item["id"];
  });
};

export const cliValidators = (): {
  readonly aggregate: ValidateFunction;
  readonly endpoints: ReadonlyMap<string, ValidateFunction>;
} => {
  const ajv = compiler();
  const aggregate = ajv.compile(schema("cli-v4.response.schema.json"));
  const validators = cliEndpointInventory().map(
    (id) => [id, ajv.compile(schema(`cli-v4.endpoint.${id}.response.schema.json`))] as const,
  );
  return { aggregate, endpoints: new Map(validators) };
};

export const requiredWebEndpoint = (value: string): WebV3EndpointId => {
  if (!isWebV3EndpointId(value)) {
    throw new Error(`Unknown corpus endpoint ${value}.`);
  }
  return value;
};

export const endpointInventory = webV3Endpoints.map((value) => value.id);

export const generatedResponse = (endpoint: WebV3EndpointId, status = 200): Response => {
  const item = webCases().find((value) => value.valid && value.endpoint === endpoint);
  if (item === undefined) {
    throw new Error(`Missing generated response fixture ${endpoint}.`);
  }
  return new Response(JSON.stringify(item.value), {
    status,
    headers: { "content-type": "application/json" },
  });
};

export const generatedWebValue = <K extends WebV3EndpointId>(endpoint: K): WebV3Response<K> => {
  const item = webCases().find((value) => value.valid && value.endpoint === endpoint);
  if (item === undefined) {
    throw new Error(`Missing validated generated response fixture ${endpoint}.`);
  }
  // The generated-contract-corpora suite validates every corpus value through the async browser
  // delivery selector. This helper only turns that checked-in positive corpus fixture into test data.
  return item.value as WebV3Response<K>;
};
