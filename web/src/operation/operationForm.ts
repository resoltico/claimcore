import { productMetadata } from "../protocol/productMetadata";
import { buildRequest } from "../protocol/client";
import type { FormValues, FormValue } from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import {
  atPath,
  applies,
  assembleCatalogInput,
  bindingPath,
  type CatalogInput,
  type CatalogEntry,
  type CatalogEntryIdentity,
} from "../protocol/catalogAssembly";
export { bindingPath, type CatalogInput } from "../protocol/catalogAssembly";

export const operationEntry = (identity: CatalogEntryIdentity): CatalogEntry => {
  const definition = productMetadata.catalog.entries.find(
    (item) => item.subject === identity.subject && item.kind === identity.kind,
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
const inputDefault = (input: CatalogInput) => {
  if (input.kind.kind === "CHOICE" && input.kind.vocabulary === "role") {
    return "";
  }
  if (input.default === null) {
    return null;
  }
  return typeof input.default === "number" ? String(input.default) : input.default;
};
const initialValue = (input: CatalogInput, current: CurrentCase | null, id: string) => {
  if (input.source === "OPERATION_IDENTITY") {
    return id;
  }
  if (input.source === "EXPECTED_REVISION") {
    return String(current?.revision ?? 0);
  }
  const preset = inputDefault(input);
  if (preset !== null) {
    return preset;
  }
  switch (input.kind.kind) {
    case "BOOLEAN":
      return false;
    case "CHOICES":
      return [];
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
  const definition = operationEntry({ subject, kind });
  const values: Record<string, FormValue> = {};
  for (const input of definition.inputs) {
    values[bindingPath(input)] = initialValue(input, current, id);
  }
  return values;
};
export const refusalPath = (
  target: string | null,
  entry: CatalogEntry,
  values: FormValues,
): string | null => {
  if (target === null) {
    return null;
  }
  return (
    entry.inputs
      .find((input) => input.diagnosticTargets.includes(target) && applies(input, values))
      ?.path.join(".") ?? null
  );
};
export const assembleOperation = (entry: CatalogEntry, values: FormValues) =>
  buildRequest("operation.execute", assembleCatalogInput(entry, values));
export const visibleOperationInputs = (
  entry: CatalogEntry,
  values: FormValues,
  current: CurrentCase | null,
) =>
  entry.inputs.filter(
    (input) =>
      input.source === "AUTHORED" &&
      !(input.field === "caseReference" && current !== null) &&
      applies(input, values),
  );
