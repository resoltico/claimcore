import type { SchemaDocument, SchemaNode } from "./schema";
export type FormValues = Readonly<Record<string, string | boolean>>;
export const resolveSchema = (document: SchemaDocument, node: SchemaNode): SchemaNode => {
  if (node.$ref !== undefined) {
    const resolved = document.components?.schemas[node.$ref.replace("#/components/schemas/", "")];
    if (resolved === undefined) {
      throw new Error("Unknown schema reference.");
    }
    return resolveSchema(document, resolved);
  }
  if (node.oneOf?.every((branch) => Object.hasOwn(branch, "const")) === true) {
    const { oneOf, ...rest } = node;
    return { ...rest, enum: oneOf.map((branch) => branch.const) };
  }
  return node;
};
const schemaConstant = (document: SchemaDocument, node: SchemaNode): unknown => {
  const resolved = resolveSchema(document, node);
  if (Object.hasOwn(resolved, "const")) {
    return resolved.const;
  }
  return resolved.enum?.length === 1 ? resolved.enum[0] : undefined;
};
export const unionSelector = (document: SchemaDocument, schema: SchemaNode): string => {
  if (schema.discriminator !== undefined) {
    return schema.discriminator.propertyName;
  }
  const branches =
    schema.oneOf?.map((branch) => resolveSchema(document, branch).properties ?? {}) ?? [];
  const first = branches[0] ?? {};
  const selector = Object.keys(first).find((name) =>
    branches.every((branch) => schemaConstant(document, branch[name] ?? {}) !== undefined),
  );
  if (selector === undefined) {
    throw new Error("Unsupported union form primitive.");
  }
  return selector;
};
export const selectedSchema = (
  document: SchemaDocument,
  node: SchemaNode,
  path: string,
  values: FormValues,
): SchemaNode => {
  const resolved = resolveSchema(document, node);
  if (resolved.oneOf === undefined) {
    return resolved;
  }
  const selector = unionSelector(document, resolved);
  const selected = values[path ? `${path}.${selector}` : selector];
  return resolveSchema(
    document,
    resolved.oneOf.find(
      (branch) =>
        schemaConstant(document, resolveSchema(document, branch).properties?.[selector] ?? {}) ===
        selected,
    ) ?? resolved.oneOf[0]!,
  );
};
