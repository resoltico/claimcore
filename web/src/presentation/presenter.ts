import { isTerm, termLabel } from "./terms";
import type { Terms } from "@contracts/messages";
import { languageDirection } from "./preferences";
import { productMetadata } from "../protocol/productMetadata";
import { correctionTargets, ruleArguments } from "@contracts/messages";
import type { Notice } from "../protocol/notices";
import type { FieldCatalogEntry } from "../api/service";
import {
  calendarDate,
  calendarDateParts,
  displayField,
  exactAmount,
  exactInteger,
  exactMoney,
  caseAge,
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
  entries: ReadonlyArray<
    CatalogText & {
      subject: string;
      kind: string;
      inputs: ReadonlyArray<
        CatalogText & { diagnosticTargets: ReadonlyArray<string>; kind: { kind: string } }
      >;
    }
  >;
}>;
const rule = (preferences: Preferences, key: string): string => {
  const declared: Readonly<Record<string, Readonly<Record<string, string | number>>>> =
    ruleArguments;
  const values = Object.fromEntries(
    Object.entries(declared[key] ?? {}).map(([name, value]) => [
      name,
      typeof value === "number" ? exactInteger(String(value), preferences.displayLocale) : value,
    ]),
  );
  return renderCatalogKey(preferences, key, values);
};
const metadata = (
  preferences: Preferences,
  owner: "field" | "act" | "group" | "authority",
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
      definition = definitions.entries.find(
        (candidate) => candidate.subject === "CASE" && candidate.kind === name,
      );
      break;
    case "authority":
      definition = definitions.entries.find(
        (candidate) => candidate.subject === "AUTHORITY" && candidate.kind === name,
      );
      break;
    case "group":
      definition = definitions.entries
        .flatMap((entry) => entry.inputs)
        .find(
          (candidate) =>
            candidate.kind.kind === "GROUP" && candidate.diagnosticTargets.includes(name),
        );
      break;
  }
  const identity = definition?.[`${part}Id`];
  if (identity === undefined || identity === null) {
    return part === "label" ? name : "";
  }
  return part === "rule" ? rule(preferences, identity) : renderCatalogKey(preferences, identity);
};
const fieldValueText = (
  preferences: Preferences,
  value: string | null,
  field: FieldCatalogEntry,
): string => {
  if (value === null) {
    return translate(preferences, "ui.notRecorded");
  }
  return field.scalar.kind === "CASE_STATUS" && isTerm("status", value)
    ? termLabel(preferences, "status", value)
    : displayField(value, field, preferences.displayLocale);
};
const correctionTargetGroup = (name: string): string | null => {
  const targets: Readonly<Record<string, string>> = correctionTargets;
  return Object.hasOwn(targets, name) ? targets[name]! : null;
};
export const createPresenter = (
  preferences: Preferences,
  definitions: PresentationDefinitions = productMetadata.catalog,
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
      : `${metadata(preferences, "group", group, "label", definitions)} · ${translate(preferences, "label.action")}`;
  },
  fieldLabel: (name: string) => metadata(preferences, "field", name, "label", definitions),
  fieldHint: (name: string) => metadata(preferences, "field", name, "hint", definitions),
  fieldRule: (name: string) => metadata(preferences, "field", name, "rule", definitions),
  authorityLabel: (name: string) => metadata(preferences, "authority", name, "label", definitions),
  authorityMeaning: (name: string) =>
    metadata(preferences, "authority", name, "meaning", definitions),
  actMeaning: (name: string) => metadata(preferences, "act", name, "meaning", definitions),
  groupLabel: (name: string) => metadata(preferences, "group", name, "label", definitions),
  groupHint: (name: string) => metadata(preferences, "group", name, "hint", definitions),
  groupRule: (name: string) => metadata(preferences, "group", name, "rule", definitions),
  actLabel: (name: string) => metadata(preferences, "act", name, "label", definitions),
  term: <V extends keyof Terms>(vocabulary: V, value: Terms[V]) =>
    termLabel(preferences, vocabulary, value),
  hint: (field: FieldCatalogEntry) =>
    field.hintId === null ? "" : renderCatalogKey(preferences, field.hintId),
  rule: (key: string) => rule(preferences, key),
  integer: (value: string | number) => exactInteger(String(value), preferences.displayLocale),
  amount: (value: string, currency?: string) =>
    exactAmount(value, preferences.displayLocale, currency),
  money: (value: string, currency: string) =>
    exactMoney(value, currency, preferences.displayLocale),
  age: (notified: string | null, businessDate: string) =>
    caseAge(notified, businessDate, preferences.displayLocale),
  recordedTime: (value: string, zone: string) =>
    recordedTime(value, zone, preferences.displayLocale),
  date: (value: string) => calendarDate(value, preferences.displayLocale),
  dateParts: (value: string) => calendarDateParts(value, preferences.displayLocale),
  dateHeading: (value: string) => calendarDate(value, preferences.displayLocale, true),
  fieldValue: (value: string | null, field: FieldCatalogEntry) =>
    fieldValueText(preferences, value, field),
});
