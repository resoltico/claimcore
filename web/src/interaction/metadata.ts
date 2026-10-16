import { productMetadata } from "@contracts/product-metadata";
import type {
  CaseFields,
  CurrentCase,
  CommandDescriptor,
  CommandInputShape,
  CommandInputDescriptor,
  CorrectionGroupDescriptor,
  FieldDescriptor,
  Descriptors,
} from "../api/service";

import {
  isCorrectionValues,
  type FlatDraftValues,
  type DraftValues,
  type CommandKind,
  type CorrectionGroupName,
} from "@contracts/request-builders";
export {
  createDraft,
  isCorrectionValues,
  type DraftValues,
  type CommandKind,
  type CorrectionGroupName,
  type CorrectionMode,
} from "@contracts/request-builders";
export const isCaseAct = (value: string): value is CommandKind =>
  productMetadata.descriptors.commands.some((command) => command.kind === value);

const correctionGroupNames: ReadonlyArray<CorrectionGroupName> =
  productMetadata.descriptors.commands.flatMap((command) =>
    command.inputs.kind === "CORRECTION_GROUPS"
      ? command.inputs.groups.map((group) => group.name)
      : [],
  );

export const isCorrectionGroupName = (value: string): value is CorrectionGroupName =>
  correctionGroupNames.some((name) => name === value);

export const correctionGroupName = (value: string): CorrectionGroupName => {
  if (!isCorrectionGroupName(value)) {
    throw new Error(`Unknown correction group ${value}.`);
  }
  return value;
};

const requiredField = (definition: Descriptors, name: string): FieldDescriptor => {
  const field = [...definition.fields, ...(definition.inputFields ?? [])].find(
    (candidate) => candidate.name === name,
  );
  if (field === undefined) {
    throw new Error(`The core definition did not describe ${name}.`);
  }
  return field;
};

const currentValue = (fields: CaseFields | undefined, input: CommandInputDescriptor): string => {
  if (input.prefill === "BLANK") {
    return "";
  }
  const value = fields?.[input.currentField];
  return value ?? "";
};

export const commandFor = (definition: Descriptors, kind: CommandKind): CommandDescriptor => {
  const command = definition.commands.find((candidate) => candidate.kind === kind);
  if (command === undefined) {
    throw new Error(`The core definition did not describe ${kind}.`);
  }
  return command;
};

const flatInputs = (shape: CommandInputShape): ReadonlyArray<CommandInputDescriptor> =>
  shape.kind === "FIELDS" ? shape.fields : [];

export const commandInputs = (
  definition: Descriptors,
  kind: CommandKind,
): { input: CommandInputDescriptor; field: FieldDescriptor }[] =>
  flatInputs(commandFor(definition, kind).inputs).map((input) => ({
    input,
    field: requiredField(definition, input.fieldName),
  }));

export const correctionGroups = (
  definition: Descriptors,
  kind: CommandKind,
): ReadonlyArray<{ group: CorrectionGroupDescriptor; fields: ReadonlyArray<FieldDescriptor> }> => {
  const shape = commandFor(definition, kind).inputs;
  if (shape.kind !== "CORRECTION_GROUPS") {
    return [];
  }
  return shape.groups.map((group) => ({
    group,
    fields: group.replaceFields.map((input) => requiredField(definition, input.fieldName)),
  }));
};

export const prefilledValues = (
  definition: Descriptors,
  kind: CommandKind,
  current: CurrentCase | null,
): DraftValues => {
  const shape = commandFor(definition, kind).inputs;
  if (shape.kind === "FIELDS") {
    return Object.fromEntries(
      shape.fields.map((input) => [input.fieldName, currentValue(current?.state.fields, input)]),
    );
  }
  const grouped = Object.fromEntries(
    shape.groups.map((group) => [
      group.name,
      {
        mode: "KEEP",
        values: Object.fromEntries(
          group.replaceFields.map((input) => [
            input.fieldName,
            currentValue(current?.state.fields, input),
          ]),
        ),
      },
    ]),
  );
  if (
    !isCorrectionValues(
      grouped,
      shape.groups.map((group) => group.name),
    )
  ) {
    throw new Error("Incomplete correction input.");
  }
  return grouped;
};

const flatValuesChanged = (values: FlatDraftValues, baseline: FlatDraftValues): boolean =>
  Object.keys(values).length !== Object.keys(baseline).length ||
  Object.entries(values).some(([field, value]) => baseline[field] !== value);

export const isDirty = (values: DraftValues, baseline: DraftValues): boolean => {
  if (isCorrectionValues(values)) {
    return (
      !isCorrectionValues(baseline) ||
      Object.keys(values).length !== Object.keys(baseline).length ||
      Object.keys(values).some((name) => {
        const group = correctionGroupName(name);
        return (
          baseline[group] === undefined ||
          values[group].mode !== baseline[group].mode ||
          flatValuesChanged(values[group].values, baseline[group].values)
        );
      })
    );
  }
  return isCorrectionValues(baseline) || flatValuesChanged(values, baseline);
};
