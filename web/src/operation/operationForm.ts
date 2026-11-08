import { operationId } from "./operationId";
import { operationEntry } from "../protocol/operationCatalog";
import { buildRequest } from "../protocol/client";
import type { FormValues, FormValue } from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import {
  atPath,
  applies,
  assembleCatalogInput,
  bindingPath,
  type CatalogInput,
  type CatalogCollection,
  type CatalogEntry,
} from "../protocol/catalogAssembly";
export { bindingPath, type CatalogInput } from "../protocol/catalogAssembly";

export const prefilledValue = (
  current: CurrentCase | null,
  path: ReadonlyArray<string> | null,
): string => {
  const value =
    path === null || current === null
      ? undefined
      : atPath(path[0] === "work" ? current : current.state.fields, path);
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
const authoredValue = (input: CatalogInput, current: CurrentCase | null) => {
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
      return input.kind.options[0] ?? "";
    case "GROUP":
      return input.required ? (input.kind.options[0] ?? "") : "";
    default:
      return prefilledValue(current, input.prefill);
  }
};
const initialValue = (
  input: CatalogInput,
  current: CurrentCase | null,
  id: string,
  subject: string,
) => {
  if (input.source === "OPERATION_IDENTITY") {
    return id;
  }
  if (input.source === "EXPECTED_REVISION") {
    return String((subject === "WORK" ? current?.work.revision : current?.revision) ?? 0);
  }
  return authoredValue(input, current);
};
export const selectableFact = (entry: CatalogEntry, input: CatalogInput): boolean =>
  entry.subject !== "AUTHORITY" &&
  entry.endpoint === "operation.execute" &&
  input.source === "AUTHORED" &&
  input.kind.kind === "TEXT" &&
  !input.required &&
  input.field !== null;

export const initialCollectionRow = (collection: CatalogCollection): FormValues =>
  Object.fromEntries(
    collection.rowInputs.map((input) => [bindingPath(input), authoredValue(input, null)]),
  );

export const initialOperationValues = (
  subject: "CASE" | "AUTHORITY" | "WORK" | "REMINDER" | "NOTE",
  kind: string,
  current: CurrentCase | null,
  id = operationId(),
): FormValues => {
  const definition = operationEntry({ subject, kind });
  const values: Record<string, FormValue> = {};
  for (const input of definition.inputs) {
    if (!selectableFact(definition, input)) {
      values[bindingPath(input)] = initialValue(input, current, id, subject);
    }
  }
  for (const collection of definition.collections) {
    values[collection.path.join(".")] = {
      kind: "COLLECTION",
      rows: Array.from({ length: collection.minimumItems }, () => ({
        key: crypto.randomUUID(),
        values: initialCollectionRow(collection),
      })),
    };
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
  const candidates = entry.inputs.filter(
    (input) => input.diagnosticTargets.includes(target) && applies(input, values),
  );
  const selected = candidates.find((candidate) => candidate.field === target) ?? candidates[0];
  if (selected !== undefined) {
    return bindingPath(selected);
  }
  const collection = entry.collections.find(
    (item) =>
      applies(item, values) &&
      (item.field === target ||
        target.startsWith(`${item.path.join(".")}.`) ||
        item.rowInputs.some((input) => input.diagnosticTargets.includes(target))),
  );
  return collection?.path.join(".") ?? null;
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
      applies(input, values) &&
      !(
        entry.subject === "WORK" &&
        current !== null &&
        !current.work.canAssign &&
        input.path.includes("responsibleActor")
      ) &&
      !(
        entry.subject === "WORK" &&
        current !== null &&
        !current.work.canUpdateSummary &&
        input.field === "handlingSummary"
      ),
  );
