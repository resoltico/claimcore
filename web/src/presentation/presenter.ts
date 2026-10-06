import correctionTargets from "./generated/correction-targets.json";
import type { Notice } from "../api/notices";
import type { FieldDescriptor } from "../api/v3";
import { calendarDate, displayField, exactAmount, exactInteger } from "./format";
import { hasMessage, renderKey, translate } from "./messages";
import { renderNotice } from "./notice";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

const metadata = (
  preferences: Preferences,
  owner: "field" | "command" | "group",
  name: string,
  part: "label" | "meaning",
): string => {
  const key = `${owner}.${name}.${part}`;
  if (hasMessage(key)) {
    return renderKey(preferences, key);
  }
  return part === "label" ? name : "";
};
const token = (preferences: Preferences, value: string): string => {
  const key = `token.${value}`;
  if (hasMessage(key)) {
    return renderKey(preferences, key);
  }
  return metadata(preferences, "command", value, "label");
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
  const s = field.scalar;
  switch (s.kind) {
    case "TEXT":
      return translate(preferences, "ui.textHint", {
        maximum: exactInteger(String(s.maximumCharacters), preferences.displayLocale),
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
export const createPresenter = (preferences: Preferences) => ({
  ...preferences,
  direction: preferences.language === "ar" ? ("rtl" as const) : ("ltr" as const),
  text: <K extends MessageKey>(key: K, ...values: Values<K>) =>
    translate(preferences, key, ...values),
  notice: (notice: Notice) => renderNotice(preferences, notice),
  correctionTargetGroup,
  authoredTargetLabel: (name: string) => {
    const group = correctionTargetGroup(name);
    return group === null
      ? metadata(preferences, "field", name, "label")
      : `${metadata(preferences, "group", group, "label")} · ${translate(preferences, "ui.action")}`;
  },
  fieldLabel: (name: string) => metadata(preferences, "field", name, "label"),
  fieldMeaning: (name: string) => metadata(preferences, "field", name, "meaning"),
  commandLabel: (name: string) => metadata(preferences, "command", name, "label"),
  commandMeaning: (name: string) => metadata(preferences, "command", name, "meaning"),
  groupLabel: (name: string) => metadata(preferences, "group", name, "label"),
  groupMeaning: (name: string) => metadata(preferences, "group", name, "meaning"),
  token: (value: string) => token(preferences, value),
  hint: (field: FieldDescriptor) => hint(preferences, field),
  integer: (value: string | number) => exactInteger(String(value), preferences.displayLocale),
  amount: (value: string) => exactAmount(value, preferences.displayLocale),
  date: (value: string) => calendarDate(value, preferences.displayLocale),
  fieldValue: (value: string | null, field: FieldDescriptor) =>
    fieldValueText(preferences, value, field),
});
