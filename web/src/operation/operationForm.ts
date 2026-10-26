import { productMetadata } from "../protocol/productMetadata";
import { wireSchemas } from "../protocol/documents";
import { buildRequest } from "../protocol/client";
import type { FormValues } from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import type { CatalogDocument } from "@contracts/client";
import {
  atPath,
  applies,
  assembleCatalogInput,
  bindingPath,
  type CatalogInput,
} from "../protocol/catalogAssembly";
export { bindingPath, type CatalogInput } from "../protocol/catalogAssembly";

// Read-only protocol summaries retain their structural document.
export const operationDocument = wireSchemas["operation.execute"].request;
export const operationEntry = (values: FormValues): CatalogDocument["entries"][number] => {
  const definition = productMetadata.catalog.entries.find(
    (item) => item.subject === values["kind"] && item.kind === values["act.kind"],
  );
  if (definition === undefined) {
    throw new Error("Unknown operation catalog entry.");
  }
  return definition;
};
const fieldValue = (current: CurrentCase | null, path: ReadonlyArray<string> | null): string => {
  const value = path === null || current === null ? undefined : atPath(current.state.fields, path);
  return typeof value === "string" ? value : "";
};
const initialValue = (input: CatalogInput, current: CurrentCase | null, id: string) => {
  if (input.source === "OPERATION_IDENTITY") {
    return id;
  }
  if (input.source === "EXPECTED_REVISION") {
    return String(current?.revision ?? 0);
  }
  if (input.default !== null) {
    return typeof input.default === "number" ? String(input.default) : input.default;
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
  subject: "CASE" | "AUTHORITY",
  kind: string,
  current: CurrentCase | null,
  id = crypto.randomUUID(),
): FormValues => {
  const definition = operationEntry({ kind: subject, "act.kind": kind });
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
    operationEntry(values)
      .inputs.find((input) => input.diagnosticTargets.includes(target) && applies(input, values))
      ?.path.join(".") ?? null
  );
};
export const assembleOperation = (values: FormValues) => {
  const entry = operationEntry(values);
  return buildRequest("operation.execute", assembleCatalogInput(entry, values));
};
export const visibleOperationInputs = (values: FormValues, current: CurrentCase | null) =>
  operationEntry(values).inputs.filter(
    (input) =>
      input.source === "AUTHORED" &&
      !(input.field === "caseReference" && current !== null) &&
      applies(input, values),
  );
