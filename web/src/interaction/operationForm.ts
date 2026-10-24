import { productMetadata } from "../protocol/productMetadata";
import { wireSchemas } from "../protocol/documents";
import { buildRequest, isObject } from "../protocol/client";
import type { FormValues } from "../protocol/forms";
import type { CurrentCase } from "../api/types";
import type { Descriptors } from "@contracts/client";

// Read-only protocol summaries retain their structural document.
export const operationDocument = wireSchemas["operation.execute"].request;
export type InteractionInput = Descriptors["interactions"][number]["inputs"][number];
const interactionFor = (values: FormValues): Descriptors["interactions"][number] => {
  const definition = productMetadata.descriptors.interactions.find(
    (item) => item.scope === values["scope"] && item.kind === values["act.kind"],
  );
  if (definition === undefined) {
    throw new Error("Unknown interaction definition.");
  }
  return definition;
};
export const bindingPath = (input: InteractionInput): string => input.path.join(".");
const applicableInput = (input: InteractionInput, values: FormValues): boolean =>
  input.branches.every((branch) => values[branch.path.join(".")] === branch.value);
const fieldValue = (current: CurrentCase | null, field: string | null): string => {
  if (current === null || field === null) {
    return "";
  }
  if (field === "caseReference") {
    return current.caseReference;
  }
  const value = Object.entries(current.state.fields).find(([name]) => name === field)?.[1];
  return typeof value === "string" ? value : "";
};
const initialValue = (input: InteractionInput, current: CurrentCase | null, id: string) => {
  if (input.source === "OPERATION_IDENTITY") {
    return id;
  }
  if (input.source === "EXPECTED_REVISION") {
    return String(current?.revision ?? 0);
  }
  switch (input.kind.kind) {
    case "BOOLEAN":
      return false;
    case "CHOICE":
    case "GROUP":
      return input.kind.options[0] ?? "";
    default:
      return fieldValue(current, input.prefill);
  }
};
export const initialOperationValues = (
  scope: "CASE" | "AUTHORITY",
  kind: string,
  current: CurrentCase | null,
  id = crypto.randomUUID(),
): FormValues => {
  const definition = interactionFor({ scope, "act.kind": kind });
  const values: Record<string, string | boolean> = {};
  for (const constant of definition.constants) {
    values[constant.path.join(".")] =
      typeof constant.value === "number" ? String(constant.value) : constant.value;
  }
  for (const input of definition.inputs) {
    values[bindingPath(input)] = initialValue(input, current, id);
  }
  return values;
};
export const refusalPath = (target: string | null, values: FormValues): string | null => {
  if (target === null) {
    return null;
  }
  return (
    interactionFor(values)
      .inputs.find(
        (input) => input.diagnosticTargets.includes(target) && applicableInput(input, values),
      )
      ?.path.join(".") ?? null
  );
};
const assign = (
  root: Record<string, unknown>,
  path: ReadonlyArray<string>,
  value: unknown,
): void => {
  let target = root;
  for (const segment of path.slice(0, -1)) {
    const child = target[segment];
    const entries: Record<string, unknown> = isObject(child)
      ? Object.fromEntries(Object.entries(child))
      : {};
    target[segment] = entries;
    target = entries;
  }
  target[path.at(-1)!] = value;
};
export const assembleOperation = (values: FormValues) => {
  const definition = interactionFor(values);
  const root: Record<string, unknown> = {};
  for (const constant of definition.constants) {
    assign(root, constant.path, constant.value);
  }
  for (const input of definition.inputs.filter((item) => applicableInput(item, values))) {
    const value = values[bindingPath(input)] ?? "";
    assign(root, input.path, input.kind.kind === "INTEGER" ? Number(value) : value);
  }
  return buildRequest("operation.execute", root);
};
export const visibleOperationInputs = (values: FormValues, current: CurrentCase | null) =>
  interactionFor(values).inputs.filter(
    (input) =>
      input.source === "AUTHORED" &&
      !(input.field === "caseReference" && current !== null) &&
      applicableInput(input, values),
  );
