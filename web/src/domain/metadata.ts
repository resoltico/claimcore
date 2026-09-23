import type {
  CaseFields,
  CaseView,
  CommandDescriptor,
  CommandDraft,
  CommandInputShape,
  CommandInputDescriptor,
  CorrectionGroupDescriptor,
  FieldDescriptor,
  SemanticDefinition,
} from "../api/v2";

type FlatDraftValues = Record<string, string>;
export type CorrectionGroupName = "registration" | "decision" | "payment";
export type CorrectionMode = "KEEP" | "REPLACE" | "CLEAR";
type CorrectionGroupValues = { mode: CorrectionMode; values: FlatDraftValues };
export type CorrectionDraftValues = Record<CorrectionGroupName, CorrectionGroupValues>;
export type DraftValues = FlatDraftValues | CorrectionDraftValues;
export type CommandKind = CommandDescriptor["kind"];

const correctionGroupNames: ReadonlyArray<CorrectionGroupName> = [
  "registration",
  "decision",
  "payment",
];

export const isCorrectionGroupName = (value: string): value is CorrectionGroupName =>
  correctionGroupNames.includes(value as CorrectionGroupName);

export const isCorrectionValues = (values: DraftValues): values is CorrectionDraftValues =>
  "registration" in values && "decision" in values && "payment" in values;

const requiredField = (definition: SemanticDefinition, name: string): FieldDescriptor => {
  const field = definition.fields.find((candidate) => candidate.name === name);
  if (field === undefined) throw new Error(`The core definition did not describe ${name}.`);
  return field;
};

const currentValue = (fields: CaseFields | undefined, input: CommandInputDescriptor): string => {
  if (input.prefill === "BLANK") return "";
  const value = fields?.[input.currentField];
  return value ?? "";
};

export const commandFor = (
  definition: SemanticDefinition,
  kind: CommandKind,
): CommandDescriptor => {
  const command = definition.commands.find((candidate) => candidate.kind === kind);
  if (command === undefined) throw new Error(`The core definition did not describe ${kind}.`);
  return command;
};

const flatInputs = (shape: CommandInputShape): ReadonlyArray<CommandInputDescriptor> =>
  shape.kind === "FIELDS" ? shape.fields : [];

export const commandInputs = (
  definition: SemanticDefinition,
  kind: CommandKind,
): { input: CommandInputDescriptor; field: FieldDescriptor }[] =>
  flatInputs(commandFor(definition, kind).inputs).map((input) => ({
    input,
    field: requiredField(definition, input.fieldName),
  }));

export const correctionGroups = (
  definition: SemanticDefinition,
  kind: CommandKind,
): ReadonlyArray<{ group: CorrectionGroupDescriptor; fields: ReadonlyArray<FieldDescriptor> }> => {
  const shape = commandFor(definition, kind).inputs;
  if (shape.kind !== "CORRECTION_GROUPS") return [];
  return shape.groups.map((group) => ({
    group,
    fields: group.replaceFields.map((input) => requiredField(definition, input.fieldName)),
  }));
};

export const prefilledValues = (
  definition: SemanticDefinition,
  kind: CommandKind,
  current: CaseView | null,
): DraftValues => {
  const shape = commandFor(definition, kind).inputs;
  if (shape.kind === "FIELDS")
    return Object.fromEntries(
      shape.fields.map((input) => [input.fieldName, currentValue(current?.fields, input)]),
    );
  return Object.fromEntries(
    shape.groups.map((group) => [
      group.name,
      {
        mode: "KEEP",
        values: Object.fromEntries(
          group.replaceFields.map((input) => [
            input.fieldName,
            currentValue(current?.fields, input),
          ]),
        ),
      },
    ]),
  ) as CorrectionDraftValues;
};

const registrationGroup = (value: CorrectionGroupValues) => {
  if (value.mode === "KEEP") return { mode: "KEEP" as const };
  if (value.mode === "CLEAR") throw new Error("Registration corrections cannot be cleared.");
  const values = value.values;
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
  if (value.mode !== "REPLACE") return { mode: value.mode };
  const values = value.values;
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
  if (value.mode !== "REPLACE") return { mode: value.mode };
  return {
    mode: "REPLACE" as const,
    values: { paymentDate: value.values["paymentDate"] ?? "" },
  };
};

export const createDraft = (
  operationId: string,
  caseReference: string,
  expectedRevision: string,
  command: CommandKind,
  values: DraftValues,
): CommandDraft => {
  if (command === "CORRECT_CASE") {
    if (!isCorrectionValues(values)) throw new Error("CORRECT_CASE requires grouped values.");
    return {
      operationId,
      caseReference,
      expectedRevision,
      command: {
        kind: "CORRECT_CASE",
        groups: {
          registration: registrationGroup(values.registration),
          decision: decisionGroup(values.decision),
          payment: paymentGroup(values.payment),
        },
      },
    };
  }
  if (isCorrectionValues(values)) throw new Error(`${command} requires flat values.`);
  return {
    operationId,
    caseReference,
    expectedRevision,
    command: { kind: command, values: { ...values } } as CommandDraft["command"],
  };
};

export const isDirty = (values: DraftValues): boolean =>
  isCorrectionValues(values)
    ? correctionGroupNames.some((group) => values[group].mode !== "KEEP")
    : Object.values(values).some((value) => value !== "");
