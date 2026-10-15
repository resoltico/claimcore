import { productMetadata } from "@contracts/product-metadata";
import type {
  CaseFields,
  CurrentCase,
  CommandDescriptor,
  CommandDraft,
  CommandInputShape,
  CommandInputDescriptor,
  CorrectionGroupDescriptor,
  FieldDescriptor,
  Descriptors,
} from "../api/service";

type FlatDraftValues = Record<string, string>;
export type CorrectionGroupName = "registration" | "decision" | "payment";
export type CorrectionMode = "KEEP" | "REPLACE" | "CLEAR";
type CorrectionGroupValues = { mode: CorrectionMode; values: FlatDraftValues };
export type CorrectionDraftValues = Record<CorrectionGroupName, CorrectionGroupValues>;
export type DraftValues = FlatDraftValues | CorrectionDraftValues;
export type CommandKind = CommandDescriptor["kind"];

export const isBusinessCommand = (value: string): value is CommandKind =>
  productMetadata.descriptors.commands.some((command) => command.kind === value);

const correctionGroupNames: ReadonlyArray<CorrectionGroupName> = [
  "registration",
  "decision",
  "payment",
];

export const isCorrectionGroupName = (value: string): value is CorrectionGroupName =>
  correctionGroupNames.some((name) => name === value);

export const correctionGroupName = (value: string): CorrectionGroupName => {
  if (!isCorrectionGroupName(value)) {
    throw new Error(`Unknown correction group ${value}.`);
  }
  return value;
};

export const isCorrectionValues = (values: DraftValues): values is CorrectionDraftValues =>
  "registration" in values && "decision" in values && "payment" in values;

const requiredField = (definition: Descriptors, name: string): FieldDescriptor => {
  const field = definition.fields.find((candidate) => candidate.name === name);
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

/** The definition names every correction group, so the entries cover the whole record. */
const correctionValues = (entries: [string, CorrectionGroupValues][]): CorrectionDraftValues =>
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion
  Object.fromEntries(entries) as CorrectionDraftValues;

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
  return correctionValues(
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
};

const registrationGroup = (value: CorrectionGroupValues) => {
  if (value.mode === "KEEP") {
    return { mode: "KEEP" as const };
  }
  if (value.mode === "CLEAR") {
    throw new Error("Registration corrections cannot be cleared.");
  }
  const { values } = value;
  return {
    mode: "REPLACE" as const,
    values: {
      incidentDate: values["incidentDate"] ?? "",
      incidentNotificationDate: values["incidentNotificationDate"] ?? "",
      incidentCountry: values["incidentCountry"] ?? "",
      claimantName: values["claimantName"] ?? "",
      insurerName: values["insurerName"] ?? "",
      claimedAmount: values["claimedAmount"] ?? "",
      claimedCurrency: values["claimedCurrency"] ?? "",
    },
  };
};

const decisionGroup = (value: CorrectionGroupValues) => {
  if (value.mode !== "REPLACE") {
    return { mode: value.mode };
  }
  const { values } = value;
  return {
    mode: "REPLACE" as const,
    values: {
      paymentDecisionDate: values["paymentDecisionDate"] ?? "",
      payableAmount: values["payableAmount"] ?? "",
      payableCurrency: values["payableCurrency"] ?? "",
    },
  };
};

const paymentGroup = (value: CorrectionGroupValues) => {
  if (value.mode !== "REPLACE") {
    return { mode: value.mode };
  }
  return {
    mode: "REPLACE" as const,
    values: { paymentDate: value.values["paymentDate"] ?? "" },
  };
};

const correctionAct = (values: DraftValues) => {
  if (!isCorrectionValues(values)) {
    throw new Error("CORRECT_CASE requires grouped values.");
  }
  return {
    kind: "CORRECT_CASE" as const,
    groups: {
      registration: registrationGroup(values.registration),
      decision: decisionGroup(values.decision),
      payment: paymentGroup(values.payment),
    },
  };
};

export const createDraft = (
  operationId: string,
  caseReference: string,
  expectedRevision: number,
  command: CommandKind,
  values: DraftValues,
): CommandDraft => {
  if (command === "CORRECT_CASE") {
    return { operationId, caseReference, expectedRevision, act: correctionAct(values) };
  }
  if (isCorrectionValues(values)) {
    throw new Error(`${command} requires flat values.`);
  }
  const common = { operationId, caseReference, expectedRevision };
  switch (command) {
    case "OPEN":
    case "AMEND_REGISTRATION": {
      const group = registrationGroup({ mode: "REPLACE", values });
      if (group.mode !== "REPLACE") {
        throw new Error("Registration requires replacement values.");
      }
      return { ...common, act: { kind: command, values: group.values } };
    }
    case "DECIDE": {
      const group = decisionGroup({ mode: "REPLACE", values });
      if (group.mode !== "REPLACE") {
        throw new Error("Decision requires replacement values.");
      }
      return { ...common, act: { kind: command, values: group.values } };
    }
    case "RECORD_PAYMENT":
      return {
        ...common,
        act: { kind: command, values: { paymentDate: values["paymentDate"] ?? "" } },
      };
    default:
      return { ...common, act: { kind: command } };
  }
};

const flatValuesChanged = (values: FlatDraftValues, baseline: FlatDraftValues): boolean =>
  Object.keys(values).length !== Object.keys(baseline).length ||
  Object.entries(values).some(([field, value]) => baseline[field] !== value);

export const isDirty = (values: DraftValues, baseline: DraftValues): boolean => {
  if (isCorrectionValues(values)) {
    return (
      !isCorrectionValues(baseline) ||
      correctionGroupNames.some(
        (group) =>
          values[group].mode !== baseline[group].mode ||
          flatValuesChanged(values[group].values, baseline[group].values),
      )
    );
  }
  return isCorrectionValues(baseline) || flatValuesChanged(values, baseline);
};
