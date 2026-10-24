import openApi from "@contracts/openapi.json" with { type: "json" };
import { endpoints, type EndpointId } from "@contracts/client";
import type { SchemaDocument, SchemaNode } from "./schema";

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
