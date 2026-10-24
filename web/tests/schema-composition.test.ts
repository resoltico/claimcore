import { expect, it } from "vitest";
import { validateSchema, type SchemaDocument, type SchemaNode } from "../src/protocol/schema";

const failure = (code: string): SchemaNode => ({
  type: "object",
  properties: { code: { const: code }, phase: { const: "NOT_STARTED" } },
  required: ["code", "phase"],
  additionalProperties: false,
});
const reply = (body: SchemaNode): SchemaNode => ({
  type: "object",
  properties: { hostFailure: body },
  required: ["hostFailure"],
  additionalProperties: false,
});

it("intersects a shared host failure with the HTTP status constraint", () => {
  const schema: SchemaDocument = {
    components: {
      schemas: { HostFailure: { oneOf: [failure("INPUT_INVALID"), failure("BODY_TOO_LARGE")] } },
    },
    allOf: [reply({ $ref: "#/components/schemas/HostFailure" }), reply(failure("INPUT_INVALID"))],
  };
  expect(
    validateSchema(schema, { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" } }),
  ).toBe(true);
  for (const value of [
    { hostFailure: { code: "BODY_TOO_LARGE", phase: "NOT_STARTED" } },
    { hostFailure: { code: "INPUT_INVALID", phase: "STARTED_UNCONFIRMED" } },
    { hostFailure: { code: "INPUT_INVALID", phase: null } },
    { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED", message: "foreign" } },
    { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" }, extra: true },
    {},
  ]) {
    expect(validateSchema(schema, value)).toBe(false);
  }
});

it("honors sibling constraints beside references, constants and combinations", () => {
  const constrained: SchemaDocument = {
    components: { schemas: { Text: { type: "string" } } },
    $ref: "#/components/schemas/Text",
    oneOf: [{ const: "allowed" }, { const: "other" }],
    allOf: [{ enum: ["allowed"] }],
    type: "string",
    minLength: 7,
  };
  expect(validateSchema(constrained, "allowed")).toBe(true);
  for (const value of ["other", "missing", 7, null]) {
    expect(validateSchema(constrained, value)).toBe(false);
  }
  expect(validateSchema({ const: "value", type: "integer" }, "value")).toBe(false);
  expect(validateSchema({ oneOf: [{ type: "string" }, { const: "value" }] }, "value")).toBe(false);
  expect(validateSchema({ $ref: "#/components/schemas/Absent" }, "value")).toBe(false);
});
