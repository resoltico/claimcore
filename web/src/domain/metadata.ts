import type {
  CaseFields,
  CaseView,
  CommandDescriptor,
  CommandDraft,
  CommandInputDescriptor,
  FieldDescriptor,
  SemanticDefinition,
} from "../api/v2";

export type DraftValues = Record<string, string>;
export type CommandKind = CommandDescriptor["kind"];

const requiredField = (definition: SemanticDefinition, name: string): FieldDescriptor => {
  const field = definition.fields.find((candidate) => candidate.name === name);
  if (field === undefined) throw new Error(`The core definition did not describe ${name}.`);
  return field;
};

const currentValue = (fields: CaseFields | undefined, input: CommandInputDescriptor): string => {
  if (input.prefill === "BLANK") return "";
  const value = fields?.[input.currentField ?? input.fieldName];
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

export const commandInputs = (
  definition: SemanticDefinition,
  kind: CommandKind,
): { input: CommandInputDescriptor; field: FieldDescriptor }[] =>
  commandFor(definition, kind).inputs.map((input) => ({
    input,
    field: requiredField(definition, input.fieldName),
  }));

export const prefilledValues = (
  definition: SemanticDefinition,
  kind: CommandKind,
  current: CaseView | null,
): DraftValues =>
  Object.fromEntries(
    commandInputs(definition, kind).map(({ input }) => [
      input.fieldName,
      currentValue(current?.fields, input),
    ]),
  );

export const createDraft = (
  operationId: string,
  caseReference: string,
  expectedRevision: string,
  command: CommandKind,
  values: DraftValues,
): CommandDraft => ({
  operationId,
  caseReference,
  expectedRevision,
  command: { kind: command, values },
});

export const presentationInputType = (field: FieldDescriptor): "date" | "text" =>
  field.scalar.kind === "CALENDAR_DATE" ? "date" : "text";

export const fieldHint = (field: FieldDescriptor): string => {
  const scalar = field.scalar;
  if (scalar.kind === "TEXT") return `Up to ${scalar.maximumCharacters} characters.`;
  if (scalar.kind === "CALENDAR_DATE") return `Use ${scalar.exactFormat}.`;
  if (scalar.kind === "AMOUNT")
    return `Exact decimal text; up to ${scalar.maximumFractionalDigits} decimal places.`;
  if (scalar.kind === "CURRENCY") return `${scalar.exactCharacters} uppercase letters.`;
  return scalar.allowedValues.join(", ");
};
