import { languageDirection } from "./preferences";
import { productMetadata } from "../protocol/productMetadata";
import correctionTargets from "./generated/correction-targets.json";
import type { Notice } from "../api/notices";
import type { FieldDescriptor } from "../api/service";
import { calendarDate, displayField, exactAmount, exactInteger } from "./format";
import { hasMessage, renderKey, translate } from "./messages";
import { renderNotice } from "./notice";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

type MessageDescriptor = Readonly<{ labelId: string; meaningId?: string }>;
export type PresentationDefinitions = Readonly<{
  fields: ReadonlyArray<MessageDescriptor & { name: string }>;
  inputFields: ReadonlyArray<MessageDescriptor & { name: string }>;
  authorityFields: ReadonlyArray<MessageDescriptor & { name: string }>;
  commands: ReadonlyArray<
    MessageDescriptor & {
      kind: string;
      inputs: { kind: string; groups?: ReadonlyArray<MessageDescriptor & { name: string }> };
    }
  >;
  authorityCommands: ReadonlyArray<MessageDescriptor & { kind: string }>;
}>;
const metadata = (
  preferences: Preferences,
  owner: "field" | "command" | "group" | "authority",
  name: string,
  part: "label" | "meaning",
  definitions: PresentationDefinitions,
): string => {
  let descriptor: MessageDescriptor | undefined;
  switch (owner) {
    case "field":
      descriptor = [
        ...definitions.fields,
        ...definitions.inputFields,
        ...definitions.authorityFields,
      ].find((candidate) => candidate.name === name);
      break;
    case "command":
      descriptor = definitions.commands.find((candidate) => candidate.kind === name);
      break;
    case "authority":
      descriptor = definitions.authorityCommands.find((candidate) => candidate.kind === name);
      break;
    case "group":
      descriptor = definitions.commands
        .flatMap((command) => command.inputs.groups ?? [])
        .find((candidate) => candidate.name === name);
      break;
  }
  const identity = part === "label" ? descriptor?.labelId : descriptor?.meaningId;
  if (identity === undefined) {
    return part === "label" ? name : "";
  }
  return renderKey(preferences, identity);
};
const token = (
  preferences: Preferences,
  value: string,
  definitions: PresentationDefinitions = productMetadata.descriptors,
): string => {
  const key = `token.${value}`;
  if (hasMessage(key)) {
    return renderKey(preferences, key);
  }
  return metadata(
    preferences,
    definitions.authorityCommands.some((item) => item.kind === value) ? "authority" : "command",
    value,
    "label",
    definitions,
  );
};
const fieldValueText = (
  preferences: Preferences,
  value: string | null,
  field: FieldDescriptor,
): string => {
  if (value === null) {
    return translate(preferences, "ui.notRecorded");
  }
  return field.scalar.kind === "CASE_STATUS"
    ? token(preferences, value)
    : displayField(value, field, preferences.displayLocale);
};
const correctionTargetGroup = (name: string): string | null => {
  const targets: Readonly<Record<string, string>> = correctionTargets;
  return Object.hasOwn(targets, name) ? targets[name]! : null;
};
const hint = (preferences: Preferences, field: FieldDescriptor): string => {
  if (field.name === "caseReference") {
    return translate(preferences, "ui.referenceHint");
  }
  if (field.name === "incidentCountry") {
    return translate(preferences, "ui.countryHint");
  }
  const s = field.scalar;
  switch (s.kind) {
    case "TEXT":
      return translate(preferences, "ui.textHint", {
        maximum: exactInteger(String(s.policy?.maximumCharacters ?? ""), preferences.displayLocale),
      });
    case "CALENDAR_DATE":
      return translate(preferences, "ui.dateHint");
    case "AMOUNT":
      return translate(preferences, "ui.amountHint", {
        maximum: exactInteger(String(s.maximumFractionalDigits), preferences.displayLocale),
      });
    case "CURRENCY":
      return translate(preferences, "ui.currencyHint", {
        length: exactInteger(String(s.exactCharacters), preferences.displayLocale),
      });
    case "CASE_STATUS":
      return s.allowedValues.map((v) => token(preferences, v)).join(", ");
  }
};
export const createPresenter = (
  preferences: Preferences,
  definitions: PresentationDefinitions = productMetadata.descriptors,
) => ({
  ...preferences,
  direction: languageDirection(preferences.language),
  text: <K extends MessageKey>(key: K, ...values: Values<K>) =>
    translate(preferences, key, ...values),
  notice: (notice: Notice) => renderNotice(preferences, notice),
  correctionTargetGroup,
  authoredTargetLabel: (name: string) => {
    const group = correctionTargetGroup(name);
    return group === null
      ? metadata(preferences, "field", name, "label", definitions)
      : `${metadata(preferences, "group", group, "label", definitions)} · ${translate(preferences, "ui.action")}`;
  },
  fieldLabel: (name: string) => metadata(preferences, "field", name, "label", definitions),
  fieldMeaning: (name: string) => metadata(preferences, "field", name, "meaning", definitions),
  authorityLabel: (name: string) => metadata(preferences, "authority", name, "label", definitions),
  commandLabel: (name: string) => metadata(preferences, "command", name, "label", definitions),
  commandMeaning: (name: string) => metadata(preferences, "command", name, "meaning", definitions),
  groupLabel: (name: string) => metadata(preferences, "group", name, "label", definitions),
  groupMeaning: (name: string) => metadata(preferences, "group", name, "meaning", definitions),
  actLabel: (value: string) => token(preferences, value, definitions),
  token: (value: string) => token(preferences, value, definitions),
  hint: (field: FieldDescriptor) => hint(preferences, field),
  integer: (value: string | number) => exactInteger(String(value), preferences.displayLocale),
  amount: (value: string) => exactAmount(value, preferences.displayLocale),
  date: (value: string) => calendarDate(value, preferences.displayLocale),
  fieldValue: (value: string | null, field: FieldDescriptor) =>
    fieldValueText(preferences, value, field),
});
