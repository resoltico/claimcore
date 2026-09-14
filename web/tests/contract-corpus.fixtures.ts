import { readFileSync } from "node:fs";
import { resolve } from "node:path";

import Ajv2020, { type AnySchema, type ValidateFunction } from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

import {
  isWebV2EndpointId,
  type WebV2EndpointId,
  webV2Endpoints,
} from "../src/generated/convergence/web-v2.endpoint-catalog";
import type { WebV2Response } from "../src/generated/convergence/web-v2.types";

type ParsedCase = {
  readonly id: string;
  readonly endpoint: string | null;
  readonly valid: boolean;
  readonly value: unknown;
};

export type CliParsedCase = ParsedCase & { readonly exitCode: number };
export type WebParsedCase = ParsedCase & { readonly status: number };

const generated = resolve(import.meta.dirname, "../src/generated/convergence");

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const readUnknown = (file: string): unknown => {
  const parsed: unknown = JSON.parse(readFileSync(resolve(generated, file), "utf8"));
  return parsed;
};

const requiredText = (value: Record<string, unknown>, name: string): string => {
  const item = value[name];
  if (typeof item !== "string" || item.length === 0)
    throw new Error(`Contract corpus ${name} must be nonempty text.`);
  return item;
};

const optionalEndpoint = (value: Record<string, unknown>): string | null => {
  const endpoint = value["endpoint"];
  if (endpoint === null || typeof endpoint === "string") return endpoint;
  throw new Error("Contract corpus endpoint must be text or null.");
};

const commonCase = (value: unknown): ParsedCase => {
  if (!isObject(value)) throw new Error("Contract corpus cases must be objects.");
  const valid = value["valid"];
  if (typeof valid !== "boolean") throw new Error("Contract corpus valid flag must be boolean.");
  return {
    id: requiredText(value, "id"),
    endpoint: optionalEndpoint(value),
    valid,
    value: value["value"],
  };
};

const corpusCases = (file: string): readonly unknown[] => {
  const document = readUnknown(file);
  if (!isObject(document) || document["schemaVersion"] !== 1 || !Array.isArray(document["cases"]))
    throw new Error("Contract corpus has an invalid envelope.");
  return document["cases"];
};

const integer = (value: unknown, name: string): number => {
  if (typeof value !== "number" || !Number.isSafeInteger(value))
    throw new Error(`Contract corpus ${name} must be a safe integer.`);
  return value;
};

export const cliCases = (): readonly CliParsedCase[] =>
  corpusCases("cli-v3.parsed-value-corpus.json").map((item) => {
    const common = commonCase(item);
    if (!isObject(item)) throw new Error("CLI corpus case must remain an object.");
    return { ...common, exitCode: integer(item["exitCode"], "exitCode") };
  });

export const rawCliInput = (identifier: string): unknown => {
  const item = corpusCases("cli-v3.raw-decoder-corpus.json").find(
    (candidate) => isObject(candidate) && candidate["id"] === identifier,
  );
  if (!isObject(item) || typeof item["bytesBase64"] !== "string")
    throw new Error(`Missing raw CLI corpus case ${identifier}.`);
  const parsed: unknown = JSON.parse(Buffer.from(item["bytesBase64"], "base64").toString("utf8"));
  if (!isObject(parsed)) throw new Error("Raw CLI corpus invocation must be an object.");
  return parsed["input"];
};

export const webCases = (): readonly WebParsedCase[] =>
  corpusCases("web-v2.parsed-value-corpus.json").map((item) => {
    const common = commonCase(item);
    if (!isObject(item)) throw new Error("Web corpus case must remain an object.");
    return { ...common, status: integer(item["status"], "status") };
  });

const schema = (file: string): AnySchema => {
  const value = readUnknown(file);
  if (!isObject(value) || typeof value["$id"] !== "string")
    throw new Error("Generated JSON Schema must be an identified object.");
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
  compiler().compile(schema("cli-v3.endpoint.command.prepare.schema.json"));

export const cliValidators = (): {
  readonly aggregate: ValidateFunction;
  readonly endpoints: ReadonlyMap<WebV2EndpointId, ValidateFunction>;
} => {
  const ajv = compiler();
  const aggregate = ajv.compile(schema("cli-v3.response.schema.json"));
  const validators = webV2Endpoints
    .map((value) => value.id)
    .filter((id) => !id.startsWith("session") && id !== "definition")
    .map((id) => [id, ajv.compile(schema(`cli-v3.endpoint.${id}.response.schema.json`))] as const);
  return { aggregate, endpoints: new Map(validators) };
};

export const requiredWebEndpoint = (value: string): WebV2EndpointId => {
  if (!isWebV2EndpointId(value)) throw new Error(`Unknown corpus endpoint ${value}.`);
  return value;
};

export const endpointInventory = webV2Endpoints.map((value) => value.id);

const localReference = (document: Record<string, unknown>, reference: string): unknown => {
  if (!reference.startsWith("#/"))
    throw new Error("Only local generated schema references are valid.");
  let current: unknown = document;
  for (const encoded of reference.slice(2).split("/")) {
    if (!isObject(current)) throw new Error("Generated schema reference does not resolve.");
    const segment = encoded.replaceAll("~1", "/").replaceAll("~0", "~");
    current = current[segment];
  }
  return current;
};

const reachableSchemaDiscriminators = (
  file: string,
  property: "kind" | "tag",
): readonly string[] => {
  const document = readUnknown(file);
  if (!isObject(document)) throw new Error("Generated response schema must be an object.");
  const tags = new Set<string>();
  const visited = new Set<string>();
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) {
      for (const item of value) visit(item);
      return;
    }
    if (!isObject(value)) return;
    const reference = value["$ref"];
    if (typeof reference === "string" && !visited.has(reference)) {
      visited.add(reference);
      visit(localReference(document, reference));
    }
    const properties = value["properties"];
    const discriminator = isObject(properties) ? properties[property] : null;
    const constant = isObject(discriminator) ? discriminator["const"] : null;
    if (typeof constant === "string") tags.add(constant);
    for (const [name, child] of Object.entries(value)) {
      if (name !== "$defs" && name !== "$ref") visit(child);
    }
  };
  visit(document);
  return [...tags].sort();
};

const valueDiscriminators = (value: unknown, property: "kind" | "tag", tags: Set<string>): void => {
  if (Array.isArray(value)) {
    for (const item of value) valueDiscriminators(item, property, tags);
    return;
  }
  if (!isObject(value)) return;
  if (typeof value[property] === "string") tags.add(value[property]);
  for (const child of Object.values(value)) valueDiscriminators(child, property, tags);
};

export const webTagCoverage = () => {
  const cases = webCases().filter((item) => item.valid);
  return endpointInventory.map((endpoint) => {
    const actual = new Set<string>();
    for (const item of cases)
      if (item.endpoint === endpoint) valueDiscriminators(item.value, "tag", actual);
    return {
      endpoint,
      expected: reachableSchemaDiscriminators(
        `web-v2.endpoint.${endpoint}.response.schema.json`,
        "tag",
      ),
      actual: [...actual].sort(),
    };
  });
};

export const cliKindCoverage = () => {
  const cases = cliCases().filter((item) => item.valid);
  const endpoints = endpointInventory.filter(
    (id) => !id.startsWith("session") && id !== "definition",
  );
  return endpoints.map((endpoint) => {
    const actual = new Set<string>();
    for (const item of cases)
      if (item.endpoint === endpoint) valueDiscriminators(item.value, "kind", actual);
    return {
      endpoint,
      expected: reachableSchemaDiscriminators(
        `cli-v3.endpoint.${endpoint}.response.schema.json`,
        "kind",
      ),
      actual: [...actual].sort(),
    };
  });
};

export const webHostCoverage = () => {
  const statuses = new Set<number>();
  const phases = new Set<string>();
  const codes = new Set<string>();
  for (const item of webCases()) {
    if (!item.valid || item.endpoint !== null || !isObject(item.value)) continue;
    statuses.add(item.status);
    const phase = item.value["executionPhase"];
    if (phase === null || typeof phase === "string") phases.add(String(phase));
    const code = item.value["code"];
    if (typeof code === "string" && code.length > 0) codes.add(code);
  }
  return {
    statuses: [...statuses].sort((left, right) => left - right),
    phases: [...phases].sort(),
    codes: [...codes].sort(),
  };
};

export const generatedResponse = (endpoint: WebV2EndpointId, status = 200): Response => {
  const item = webCases().find((value) => value.valid && value.endpoint === endpoint);
  if (item === undefined) throw new Error(`Missing generated response fixture ${endpoint}.`);
  return new Response(JSON.stringify(item.value), {
    status,
    headers: { "content-type": "application/json" },
  });
};

export const generatedWebValue = <K extends WebV2EndpointId>(endpoint: K): WebV2Response<K> => {
  const item = webCases().find((value) => value.valid && value.endpoint === endpoint);
  if (item === undefined)
    throw new Error(`Missing validated generated response fixture ${endpoint}.`);
  // The generated-contract-corpora suite validates every corpus value through the async browser
  // delivery selector. This helper only turns that checked-in positive corpus fixture into test data.
  return item.value as WebV2Response<K>;
};
