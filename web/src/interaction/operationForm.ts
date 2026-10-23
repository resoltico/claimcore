import { productMetadata } from "../protocol/productMetadata";
import { wireSchemas } from "../protocol/client";
import {
  resolveSchema,
  selectedSchema,
  unionSelector,
  schemaConstant,
  type FormValues,
} from "../protocol/forms";
import type { SchemaDocument, SchemaNode } from "../protocol/schema";
import type { CurrentCase } from "../api/types";

export const operationDocument = wireSchemas["operation.execute"].request;
const childPath = (parent: string, child: string): string =>
  parent ? `${parent}.${child}` : child;
type Choice = { path: string; selector: string; options: ReadonlyArray<unknown> };

const choices = (
  document: SchemaDocument,
  node: SchemaNode,
  path: string,
  values: FormValues,
): Choice[] => {
  const resolved = resolveSchema(document, node);
  const own: Choice[] =
    resolved.oneOf === undefined
      ? []
      : [
          {
            path,
            selector: unionSelector(document, resolved),
            options: resolved.oneOf.map((branch) =>
              schemaConstant(
                document,
                resolveSchema(document, branch).properties?.[unionSelector(document, resolved)] ??
                  {},
              ),
            ),
          },
        ];
  const selected = selectedSchema(document, resolved, path, values);
  return own.concat(
    Object.entries(selected.properties ?? {}).flatMap(([key, value]) =>
      choices(document, value, childPath(path, key), values),
    ),
  );
};
const operationChoices = (values: FormValues): Choice[] =>
  choices(operationDocument, operationDocument, "", values);

const leaves = (node: SchemaNode, path: string, values: FormValues): string[] => {
  const selected = selectedSchema(operationDocument, node, path, values);
  if (selected.type === "object") {
    return Object.entries(selected.properties ?? {}).flatMap(([key, value]) =>
      leaves(value, childPath(path, key), values),
    );
  }
  return Object.hasOwn(selected, "const") ? [] : [path];
};
const operationPaths = (values: FormValues): string[] => leaves(operationDocument, "", values);

const prefill = (
  current: CurrentCase | null,
  input: { prefill: string; currentField?: string },
): string => {
  if (input.prefill !== "CURRENT_FIELD" || current === null || input.currentField === undefined) {
    return "";
  }
  const entry = Object.entries(current.state.fields).find(([key]) => key === input.currentField);
  return typeof entry?.[1] === "string" ? entry[1] : "";
};
const setFields = (
  values: Record<string, string | boolean>,
  current: CurrentCase | null,
  fields: ReadonlyArray<{ fieldName: string; prefill: string; currentField?: string }>,
): void => {
  const paths = operationPaths(values);
  for (const input of fields) {
    const path = paths.find((candidate) => candidate.split(".").at(-1) === input.fieldName);
    if (path === undefined) {
      throw new Error("Descriptor input is absent from its registry schema.");
    }
    values[path] = prefill(current, input);
  }
};
export const initialOperationValues = (
  scope: "CASE" | "AUTHORITY",
  kind: string,
  current: CurrentCase | null,
  id = crypto.randomUUID(),
): FormValues => {
  const values: Record<string, string | boolean> = { scope, operationId: id, "act.kind": kind };
  if (scope === "AUTHORITY") {
    return values;
  }
  values["caseReference"] = current?.caseReference ?? "";
  values["expectedRevision"] = String(current?.revision ?? 0);
  const command = productMetadata.descriptors.commands.find((candidate) => candidate.kind === kind);
  if (command === undefined) {
    throw new Error("Unknown case act descriptor.");
  }
  if (command.inputs.kind === "FIELDS") {
    setFields(values, current, command.inputs.fields);
  } else {
    const modes = operationChoices(values).filter((choice) => choice.selector === "mode");
    for (const group of command.inputs.groups) {
      const choice = modes.find((candidate) => candidate.path.split(".").at(-1) === group.name);
      if (choice === undefined) {
        throw new Error("Correction group is absent from its schema.");
      }
      const selector = childPath(choice.path, choice.selector);
      values[selector] = "REPLACE";
      setFields(values, current, group.replaceFields);
      values[selector] = "KEEP";
    }
  }
  return values;
};
export const refusalPath = (target: string | null, values: FormValues): string | null => {
  if (target === null) {
    return null;
  }
  if (Object.hasOwn(values, target)) {return target;}
  if (target.startsWith("principal.")) {
    return target.replace("principal.", "target.");
  }
  return (
    operationPaths(values).find((path) => path.split(".").at(-1) === target) ??
    operationChoices(values)
      .filter((choice) => choice.selector === "mode")
      .map((choice) => childPath(choice.path, choice.selector))
      .find((path) => path.split(".").at(-2) === target) ??
    null
  );
};
const operationFormSchema = (values: FormValues, current: CurrentCase | null): SchemaNode => {
  const schema = selectedSchema(operationDocument, operationDocument, "", values);
  return {
    ...schema,
    properties: Object.fromEntries(
      Object.entries(schema.properties ?? {}).filter(
        ([key]) =>
          !["operationId", "expectedRevision", "scope"].includes(key) &&
          !(key === "caseReference" && current !== null),
      ),
    ),
  };
};

const orderedMembers = (node: SchemaNode): [string, SchemaNode][] => {
  const fields = [
    ...productMetadata.descriptors.fields,
    ...productMetadata.descriptors.inputFields,
  ];
  const order = (name: string) => {
    const index = fields.findIndex((field) => field.name === name);
    return index < 0 ? fields.length : index;
  };
  return Object.entries(node.properties ?? {}).sort(
    ([left], [right]) => order(left) - order(right),
  );
};
const allowedBranches = (
  schema: SchemaNode,
  path: string,
  values: FormValues,
  current: CurrentCase | null,
): ReadonlyArray<SchemaNode> => {
  const offer = current?.advice.acts.find((item) => item.act === values["act.kind"]);
  const allowed =
    offer !== undefined && "correction" in offer
      ? Object.entries(offer.correction).find(([name]) => name === path.split(".").at(-1))?.[1]
      : undefined;
  const selector = unionSelector(operationDocument, schema);
  return schema.oneOf!.filter(
    (branch) =>
      allowed === undefined ||
      allowed.some(
        (mode) =>
          mode ===
          schemaConstant(
            operationDocument,
            resolveSchema(operationDocument, branch).properties?.[selector] ?? {},
          ),
      ),
  );
};
const formNode = (
  node: SchemaNode,
  path: string,
  values: FormValues,
  current: CurrentCase | null,
): SchemaNode => {
  const schema = resolveSchema(operationDocument, node);
  if (schema.oneOf !== undefined) {
    if (path === "act") {
      return formNode(
        selectedSchema(operationDocument, schema, path, values),
        path,
        values,
        current,
      );
    }
    return {
      ...schema,
      oneOf: allowedBranches(schema, path, values, current).map((branch) =>
        formNode(branch, path, values, current),
      ),
    };
  }
  if (schema.type !== "object") {
    return schema;
  }
  return {
    ...schema,
    properties: Object.fromEntries(
      orderedMembers(schema).map(([key, value]) => [
        key,
        formNode(value, childPath(path, key), values, current),
      ]),
    ),
  };
};
export const visibleOperationSchema = (
  values: FormValues,
  current: CurrentCase | null,
): SchemaNode => {
  const schema = operationFormSchema(values, current);
  return formNode(
    {
      ...schema,
      required: schema.required?.filter((key) => Object.hasOwn(schema.properties ?? {}, key)) ?? [],
    },
    "",
    values,
    current,
  );
};
