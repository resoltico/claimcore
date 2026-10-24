import { strictByteProfile } from "@contracts/client";
import { strictJsonBytes } from "./strictJson";

export type SchemaNode = {
  readonly type?: string;
  readonly $ref?: string;
  readonly const?: unknown;
  readonly enum?: ReadonlyArray<unknown>;
  readonly oneOf?: ReadonlyArray<SchemaNode>;
  readonly discriminator?: { readonly propertyName: string };
  readonly allOf?: ReadonlyArray<SchemaNode>;
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
  readonly components?: { readonly schemas: Readonly<Record<string, SchemaNode>> };
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

export { isGregorianDate } from "@contracts/client";
