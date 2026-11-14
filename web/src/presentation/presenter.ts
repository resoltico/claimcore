import { taskDefinitions } from "@contracts/client";
import { isTerm, termLabel } from "./terms";
import type { Terms } from "@contracts/messages";
import { languageDirection } from "./preferences";
import { productMetadata } from "../protocol/productMetadata";
import { ruleArguments, inputRuleArguments } from "@contracts/messages";
import type { Notice } from "../protocol/notices";
import type { FieldCatalogEntry } from "../api/service";
import {
  calendarDate,
  calendarDateParts,
  displayField,
  exactAmount,
  exactInteger,
  exactMoney,
  recordedTime,
} from "./format";
import { renderCatalogKey, translate } from "./messages";
import { renderNotice } from "./notice";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

type CatalogText = Readonly<{
  labelId: string;
  hintId?: string | null;
  ruleId?: string | null;
  meaningId?: string | null;
}>;
export type PresentationDefinitions = Readonly<{
  fields: ReadonlyArray<CatalogText & { name: string }>;
  inputFields: ReadonlyArray<CatalogText & { name: string }>;
}>;
const rule = (preferences: Preferences, key: string, inputName: string | null = null): string => {
  const declared: Readonly<Record<string, Readonly<Record<string, string | number>>>> =
    ruleArguments;
  const inputs: Readonly<Record<string, Readonly<Record<string, string | number>>>> =
    inputRuleArguments;
  const values = Object.fromEntries(
    Object.entries((inputName === null ? undefined : inputs[inputName]) ?? declared[key] ?? {}).map(
      ([name, value]) => [
        name,
        typeof value === "number" ? exactInteger(String(value), preferences.displayLocale) : value,
      ],
    ),
  );
  return renderCatalogKey(preferences, key, values);
};
const metadata = (
  preferences: Preferences,
  owner: "field" | "act" | "people",
  name: string,
  part: "label" | "hint" | "rule" | "meaning",
  definitions: PresentationDefinitions,
): string => {
  let definition: CatalogText | undefined;
  switch (owner) {
    case "field":
      definition = [...definitions.fields, ...definitions.inputFields].find(
        (candidate) => candidate.name === name,
      );
      break;
    case "act":
      definition = taskDefinitions.find(
        (candidate) =>
          candidate.kind === "OPERATION" && candidate.subject === "CASE" && candidate.act === name,
      );
      break;
    case "people":
      definition = taskDefinitions.find(
        (candidate) =>
          candidate.kind === "OPERATION" &&
          candidate.subject === "PEOPLE" &&
          candidate.act === name,
      );
      break;
  }
  const identity = definition?.[`${part}Id`];
  if (identity === undefined || identity === null) {
    return part === "label" ? name : "";
  }
  return part === "rule"
    ? rule(preferences, identity, name)
    : renderCatalogKey(preferences, identity);
};
const fieldValueText = (
  preferences: Preferences,
  value: string | null,
  field: FieldCatalogEntry,
): string => {
  if (value === null) {
    return translate(preferences, "label.notRecorded");
  }
  return field.scalar.kind === "CASE_STATUS" && isTerm("status", value)
    ? termLabel(preferences, "status", value)
    : displayField(value, field, preferences.displayLocale);
};
export const createPresenter = (
  preferences: Preferences,
  definitions: PresentationDefinitions = productMetadata.catalog,
) => ({
  ...preferences,
  direction: languageDirection(preferences.language),
  text: <K extends MessageKey>(key: K, ...values: Values<K>) =>
    translate(preferences, key, ...values),
  notice: (notice: Exclude<Notice, { kind: "human" | "humanIssues" }>) =>
    renderNotice(preferences, notice),
  fieldLabel: (name: string) => metadata(preferences, "field", name, "label", definitions),
  fieldHint: (name: string) => metadata(preferences, "field", name, "hint", definitions),
  fieldRule: (name: string) => metadata(preferences, "field", name, "rule", definitions),
  peopleLabel: (name: string) => metadata(preferences, "people", name, "label", definitions),
  peopleMeaning: (name: string) => metadata(preferences, "people", name, "meaning", definitions),
  actMeaning: (name: string) => metadata(preferences, "act", name, "meaning", definitions),
  actLabel: (name: string) => metadata(preferences, "act", name, "label", definitions),
  term: <V extends keyof Terms>(vocabulary: V, value: Terms[V]) =>
    termLabel(preferences, vocabulary, value),
  hint: (field: FieldCatalogEntry) =>
    field.hintId === null ? "" : renderCatalogKey(preferences, field.hintId),
  rule: (key: string, name: string | null) => rule(preferences, key, name),
  integer: (value: string | number) => exactInteger(String(value), preferences.displayLocale),
  amount: (value: string, currency?: string) =>
    exactAmount(value, preferences.displayLocale, currency),
  money: (value: string, currency: string) =>
    exactMoney(value, currency, preferences.displayLocale),
  recordedTime: (value: string, zone: string) =>
    recordedTime(value, zone, preferences.displayLocale),
  date: (value: string) => calendarDate(value, preferences.displayLocale),
  dateParts: (value: string) => calendarDateParts(value, preferences.displayLocale),
  dateHeading: (value: string) => calendarDate(value, preferences.displayLocale, true),
  fieldValue: (value: string | null, field: FieldCatalogEntry) =>
    fieldValueText(preferences, value, field),
});
