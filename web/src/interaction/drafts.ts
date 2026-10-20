import requestBindings from "@contracts/request-bindings.json" with { type: "json" };
import wireDocument from "@contracts/openapi.json" with { type: "json" };
import { validateStructure } from "../protocol/schema";
import type { RequestByEndpoint, Descriptors } from "@contracts/client";
export type CommandKind = RequestByEndpoint["case.execute"]["act"]["kind"];
type GroupedInputs = Extract<
  Descriptors["commands"][number]["inputs"],
  { kind: "CORRECTION_GROUPS" }
>;
export type CorrectionGroupName = GroupedInputs["groups"][number]["name"];
export type CorrectionMode = GroupedInputs["groups"][number]["actions"][number];
export type FlatDraftValues = Record<string, string>;
type CorrectionGroupValues = { mode: CorrectionMode; values: FlatDraftValues };
export type GroupedDraftValues = Record<string, CorrectionGroupValues>;
export type DraftValues = FlatDraftValues | GroupedDraftValues;
const isFlatValues = (value: unknown): value is FlatDraftValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.values(value).every((entry) => typeof entry === "string");
const correctionActions = requestBindings.flatMap((binding) =>
  (binding.groups ?? []).flatMap((group) => group.actions),
);
const isCorrectionMode = (value: unknown): value is CorrectionMode =>
  typeof value === "string" && correctionActions.includes(value);
const isGroupValues = (value: unknown): value is CorrectionGroupValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  "mode" in value &&
  isCorrectionMode(value.mode) &&
  "values" in value &&
  isFlatValues(value.values);
export const isGroupedDraftValues = (value: unknown): value is GroupedDraftValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.values(value).length > 0 &&
  Object.values(value).every(isGroupValues);
export const hasRequiredCorrectionGroups = (
  value: unknown,
  names: ReadonlyArray<string>,
): value is GroupedDraftValues =>
  isGroupedDraftValues(value) && names.every((name) => Object.hasOwn(value, name));
type Act = RequestByEndpoint["case.execute"]["act"];
type FieldBinding = Readonly<{ fieldName: string; path: ReadonlyArray<string> }>;
type GroupBinding = Readonly<{
  name: string;
  path: ReadonlyArray<string>;
  actions: ReadonlyArray<string>;
  fields: ReadonlyArray<FieldBinding>;
}>;
type Binding = Readonly<{
  kind: string;
  fields?: ReadonlyArray<FieldBinding>;
  groups?: ReadonlyArray<GroupBinding>;
}>;
const bindings: ReadonlyArray<Binding> = requestBindings;
const assign = (
  target: Record<string, unknown>,
  path: ReadonlyArray<string>,
  value: unknown,
): void => {
  const [head, ...tail] = path;
  if (head === undefined || ["__proto__", "constructor", "prototype"].includes(head)) {
    throw new Error("Unsupported request binding path.");
  }
  if (tail.length === 0) {
    target[head] = value;
    return;
  }
  const child = target[head] ?? {};
  if (typeof child !== "object" || child === null || Array.isArray(child)) {
    throw new Error("Contradictory request bindings.");
  }
  const nested = Object.fromEntries(Object.entries(child));
  target[head] = nested;
  assign(nested, tail, value);
};
const writeFields = (
  target: Record<string, unknown>,
  fields: ReadonlyArray<FieldBinding>,
  values: FlatDraftValues,
): void => {
  for (const field of fields) {
    assign(target, field.path, values[field.fieldName] ?? "");
  }
};
const isAct = (value: unknown): value is Act =>
  validateStructure({ ...wireDocument, $ref: "#/components/schemas/CaseAct" }, value);
export const assembleBoundAct = (
  binding: Binding,
  values: DraftValues,
): Record<string, unknown> => {
  const result: Record<string, unknown> = { kind: binding.kind };
  if (binding.groups === undefined) {
    if (isGroupedDraftValues(values) || binding.fields === undefined) {
      throw new Error("Operation requires flat values and complete bindings.");
    }
    writeFields(result, binding.fields, values);
  } else {
    if (
      !hasRequiredCorrectionGroups(
        values,
        binding.groups.map((group) => group.name),
      )
    ) {
      throw new Error("Correction requires grouped values.");
    }
    for (const group of binding.groups) {
      const input = values[group.name]!;
      if (!group.actions.includes(input.mode)) {
        throw new Error("Unsupported correction choice.");
      }
      assign(result, [...group.path, "mode"], input.mode);
      if (input.mode === "REPLACE") {
        writeFields(result, group.fields, input.values);
      }
    }
  }
  return result;
};
export const assembleAct = (binding: Binding, values: DraftValues): Act => {
  const result = assembleBoundAct(binding, values);
  if (!isAct(result)) {
    throw new Error("Request binding does not satisfy its typed codec.");
  }
  return result;
};
export const createDraft = (
  operationId: string,
  caseReference: string,
  expectedRevision: number,
  command: CommandKind,
  values: DraftValues,
): RequestByEndpoint["case.execute"] => {
  const binding = bindings.find((item) => item.kind === command);
  if (binding === undefined) {
    throw new Error("Unsupported case act binding.");
  }
  return { operationId, caseReference, expectedRevision, act: assembleAct(binding, values) };
};
