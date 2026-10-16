import { strictByteProfile } from "@contracts/endpoint-catalog";
import { strictJsonBytes, isRepresentable } from "./strictJson";

export type SchemaNode = {
  readonly type?: string;
  readonly $ref?: string;
  readonly const?: unknown;
  readonly enum?: ReadonlyArray<unknown>;
  readonly oneOf?: ReadonlyArray<SchemaNode>;
  readonly properties?: Readonly<Record<string, SchemaNode>>;
  readonly required?: ReadonlyArray<string>;
  readonly additionalProperties?: boolean | SchemaNode;
  readonly items?: SchemaNode;
  readonly prefixItems?: ReadonlyArray<SchemaNode>;
  readonly minItems?: number;
  readonly maxItems?: number;
  readonly minimum?: number;
  readonly maximum?: number;
  readonly minLength?: number;
  readonly maxLength?: number;
  readonly format?: string;
  readonly pattern?: string;
};
export type SchemaDocument = SchemaNode & {
  readonly $defs?: Readonly<Record<string, SchemaNode>>;
  readonly $schema?: string;
  readonly $id?: string;
  readonly title?: string;
};

// The strict profile preserves integer lexical spelling before JSON.parse loses it.
export const admitBytes = (bytes: Uint8Array): unknown => {
  if (bytes.length > strictByteProfile.maximumResponseBytes) {
    throw new Error("JSON exceeds its byte allowance.");
  }
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  const tokens =
    text.match(/"(?:[^"\\]|\\.)*"|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/gu) ?? [];
  if (tokens.some((token) => !token.startsWith('"') && /[.eE]/u.test(token))) {
    throw new Error("Integer wire values require integer spelling.");
  }
  return strictJsonBytes(bytes);
};

export const isGregorianDate = (text: string): boolean => {
  if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/u.test(text) || text.startsWith("0000")) {
    return false;
  }
  const date = new Date(`${text}T00:00:00.000Z`);
  return Number.isFinite(date.valueOf()) && date.toISOString().slice(0, 10) === text;
};
const formatMatches = (format: string | undefined, value: string): boolean => {
  switch (format) {
    case undefined:
      return true;
    case "uuid":
      return /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(
        value,
      );
    case "date":
      return isGregorianDate(value);
    case "date-time":
      return (
        isGregorianDate(value.slice(0, 10)) &&
        /^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt](?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]+)?(?:[Zz]|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])$/u.test(
          value,
        ) &&
        Number.isFinite(Date.parse(value))
      );
    default:
      return false;
  }
};
const stringMatches = (schema: SchemaNode, value: string): boolean => {
  const count = Array.from(value).length;
  return (
    isRepresentable(value) &&
    (schema.minLength === undefined || count >= schema.minLength) &&
    (schema.maxLength === undefined || count <= schema.maxLength) &&
    formatMatches(schema.format, value) &&
    (schema.pattern === undefined || new RegExp(schema.pattern, "u").test(value))
  );
};
const arrayMatches = (
  document: SchemaDocument,
  schema: SchemaNode,
  value: ReadonlyArray<unknown>,
): boolean => {
  if (
    (schema.minItems !== undefined && value.length < schema.minItems) ||
    (schema.maxItems !== undefined && value.length > schema.maxItems)
  ) {
    return false;
  }
  if (schema.prefixItems !== undefined) {
    return (
      value.length === schema.prefixItems.length &&
      schema.prefixItems.every((item, index) => check(document, item, value[index]))
    );
  }
  return schema.items !== undefined && value.every((item) => check(document, schema.items!, item));
};
const objectMatches = (document: SchemaDocument, schema: SchemaNode, value: object): boolean => {
  const members = Object.entries(value);
  if (schema.required?.some((name) => !Object.hasOwn(value, name)) === true) {
    return false;
  }
  return members.every(([name, item]) => {
    const declared = schema.properties?.[name];
    if (declared !== undefined) {
      return check(document, declared, item);
    }
    return typeof schema.additionalProperties === "object"
      ? check(document, schema.additionalProperties, item)
      : schema.additionalProperties !== false;
  });
};
function check(document: SchemaDocument, schema: SchemaNode, value: unknown): boolean {
  if (schema.$ref !== undefined) {
    const target = document.$defs?.[schema.$ref.replace("#/$defs/", "")];
    return target !== undefined && check(document, target, value);
  }
  if (Object.hasOwn(schema, "const")) {
    return value === schema.const;
  }
  if (schema.enum !== undefined) {
    return schema.enum.includes(value);
  }
  if (schema.oneOf !== undefined) {
    return schema.oneOf.filter((item) => check(document, item, value)).length === 1;
  }
  return primitiveMatches(document, schema, value);
}
function primitiveMatches(document: SchemaDocument, schema: SchemaNode, value: unknown): boolean {
  switch (schema.type) {
    case "string":
      return typeof value === "string" && stringMatches(schema, value);
    case "integer":
      return integerMatches(schema, value);
    case "boolean":
      return typeof value === "boolean";
    case "null":
      return value === null;
    case "array":
      return Array.isArray(value) && arrayMatches(document, schema, value);
    case "object":
      return objectValue(document, schema, value);
    default:
      return false;
  }
}
function integerMatches(schema: SchemaNode, value: unknown): boolean {
  return (
    typeof value === "number" &&
    Number.isSafeInteger(value) &&
    (schema.minimum === undefined || value >= schema.minimum) &&
    (schema.maximum === undefined || value <= schema.maximum)
  );
}
function objectValue(document: SchemaDocument, schema: SchemaNode, value: unknown): boolean {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    objectMatches(document, schema, value)
  );
}
export const validateSchema = (document: SchemaDocument, value: unknown): boolean =>
  check(document, document, value);

// Authored drafts may be incomplete; assembly verifies shape, byte admission verifies wire syntax.
const structuralNode = (schema: SchemaNode): SchemaNode => {
  if (schema.type === "string") {
    return { type: "string" };
  }
  if (schema.type === "integer") {
    return { type: "integer" };
  }
  if (schema.properties !== undefined) {
    return {
      ...schema,
      properties: Object.fromEntries(
        Object.entries(schema.properties).map(([name, child]) => [name, structuralNode(child)]),
      ),
    };
  }
  if (schema.oneOf !== undefined) {
    return { ...schema, oneOf: schema.oneOf.map(structuralNode) };
  }
  if (schema.items !== undefined) {
    return { ...schema, items: structuralNode(schema.items) };
  }
  return schema;
};
export const validateStructure = (document: SchemaDocument, value: unknown): boolean =>
  validateSchema(
    {
      ...structuralNode(document),
      $defs: Object.fromEntries(
        Object.entries(document.$defs ?? {}).map(([name, child]) => [name, structuralNode(child)]),
      ),
    },
    value,
  );

function issueAt(
  document: SchemaDocument,
  node: SchemaNode,
  value: unknown,
  path: ReadonlyArray<string>,
): ReadonlyArray<string> | null {
  if (check(document, node, value)) {
    return null;
  }
  if (node.$ref !== undefined) {
    return issueAt(
      document,
      document.$defs?.[node.$ref.replace("#/$defs/", "")] ?? {},
      value,
      path,
    );
  }
  if (node.oneOf !== undefined) {
    return unionIssue(document, node, value, path);
  }
  if (
    node.type === "object" &&
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value)
  ) {
    return objectIssue(document, node, value, path);
  }
  return path;
}
function objectIssue(
  document: SchemaDocument,
  node: SchemaNode,
  value: object,
  path: ReadonlyArray<string>,
): ReadonlyArray<string> {
  const members = new Map(Object.entries(value));
  for (const [name, child] of Object.entries(node.properties ?? {})) {
    if (!members.has(name) && node.required?.includes(name) === true) {
      return [...path, name];
    }
    if (members.has(name)) {
      const issue = issueAt(document, child, members.get(name), [...path, name]);
      if (issue !== null) {
        return issue;
      }
    }
  }
  return path;
}
function unionIssue(
  document: SchemaDocument,
  node: SchemaNode,
  value: unknown,
  path: ReadonlyArray<string>,
): ReadonlyArray<string> {
  if (typeof value !== "object" || value === null) {
    return path;
  }
  const members = new Map(Object.entries(value));
  const branch = node.oneOf?.find((candidate) =>
    Object.entries(candidate.properties ?? {}).some(
      ([name, child]) => Object.hasOwn(child, "const") && members.get(name) === child.const,
    ),
  );
  return branch === undefined ? path : (issueAt(document, branch, value, path) ?? path);
}
export const schemaIssue = (
  document: SchemaDocument,
  value: unknown,
): ReadonlyArray<string> | null => issueAt(document, document, value, []);
