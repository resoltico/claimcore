import openApi from "@contracts/openapi.json" with { type: "json" };
import { endpoints, type EndpointId } from "@contracts/client";
import type { HostFailure, RequestByEndpoint, ResponseByEndpoint } from "@contracts/client";
import {
  admitBytes,
  validateSchema,
  validateStructure,
  schemaIssue,
  type SchemaDocument,
  type SchemaNode,
} from "./schema";
import { correlateReply } from "./correlation";
export { correlateReply };

type Operation = {
  requestBody?: { content: { "application/json": { schema: SchemaNode } } };
  responses: Record<string, { content: { "application/json": { schema: SchemaNode } } }>;
};
type ApiDocument = {
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, SchemaNode> };
};
const document: ApiDocument = openApi;
const scoped = (schema: SchemaNode): SchemaDocument => ({
  ...schema,
  components: document.components,
});
type SchemaPair = { request: SchemaDocument; response: SchemaDocument };
const schemaTable: Record<string, SchemaPair> = Object.fromEntries(
  endpoints.map((endpoint) => {
    const operation = document.paths[endpoint.path]![endpoint.method.toLowerCase()]!;
    return [
      endpoint.id,
      {
        request: scoped(
          operation.requestBody?.content["application/json"].schema ?? {
            type: "object",
            properties: {},
            additionalProperties: false,
          },
        ),
        response: scoped(operation.responses["200"]!.content["application/json"].schema),
      },
    ];
  }),
);
const complete = (table: Record<string, SchemaPair>): table is Record<EndpointId, SchemaPair> =>
  endpoints.every(({ id }) => table[id] !== undefined);
if (!complete(schemaTable)) {
  throw new Error("OpenAPI omitted an endpoint schema.");
}
export const wireSchemas: Record<EndpointId, SchemaPair> = schemaTable;
const first = document.paths[endpoints[0].path]![endpoints[0].method.toLowerCase()]!;
const hostSchemas: Record<string, SchemaDocument> = Object.fromEntries(
  Object.entries(first.responses)
    .filter(([status]) => status !== "200")
    .map(([status, response]) => [status, scoped(response.content["application/json"].schema)]),
);
export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export const isResponse = <K extends EndpointId>(
  endpoint: K,
  value: unknown,
): value is ResponseByEndpoint[K] => validateSchema(wireSchemas[endpoint].response, value);
export const decodeResponse = <K extends EndpointId>(
  endpoint: K,
  bytes: Uint8Array,
): ResponseByEndpoint[K] => {
  const value = admitBytes(bytes);
  if (!isResponse(endpoint, value)) {
    throw new Error("Invalid endpoint response.");
  }
  return value;
};
export const isHostFailure = (
  value: unknown,
  status: number,
): value is { readonly hostFailure: HostFailure } => {
  const schema = hostSchemas[String(status)];
  return schema !== undefined && validateSchema(schema, value);
};
export const buildRequest = <K extends EndpointId>(
  endpoint: K,
  value: unknown,
): RequestByEndpoint[K] => {
  const matches = (candidate: unknown): candidate is RequestByEndpoint[K] =>
    validateStructure(wireSchemas[endpoint].request, candidate);
  if (!matches(value)) {
    throw new Error("Invalid endpoint request structure.");
  }
  return value;
};
export const requestIssue = (endpoint: EndpointId, value: unknown): ReadonlyArray<string> | null =>
  schemaIssue(wireSchemas[endpoint].request, value);
export const isRequest = <K extends EndpointId>(
  endpoint: K,
  value: unknown,
): value is RequestByEndpoint[K] => validateSchema(wireSchemas[endpoint].request, value);

export const isEndpointId = (value: string): value is EndpointId =>
  endpoints.some((item) => item.id === value);
