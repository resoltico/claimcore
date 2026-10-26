import { languageDirection } from "./preferences";
import { productMetadata } from "../protocol/productMetadata";
import { correctionTargets } from "@contracts/messages";
import type { Notice } from "../api/notices";
import type { FieldCatalogEntry } from "../api/service";
import { calendarDate, displayField, exactAmount, exactInteger } from "./format";
import { hasMessage, renderKey, translate } from "./messages";
import { renderNotice } from "./notice";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

type CatalogText = Readonly<{ labelId: string; meaningId?: string | null }>;
export type PresentationDefinitions = Readonly<{
  fields: ReadonlyArray<CatalogText & { name: string }>;
  inputFields: ReadonlyArray<CatalogText & { name: string }>;
  authorityFields: ReadonlyArray<CatalogText & { name: string }>;
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
const metadata = (
  preferences: Preferences,
  owner: "field" | "act" | "group" | "authority",
  name: string,
  part: "label" | "meaning",
  definitions: PresentationDefinitions,
): string => {
  let definition: CatalogText | undefined;
  switch (owner) {
    case "field":
      definition = [
        ...definitions.fields,
        ...definitions.inputFields,
        ...definitions.authorityFields,
      ].find((candidate) => candidate.name === name);
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
  const identity = part === "label" ? definition?.labelId : definition?.meaningId;
  if (identity === undefined || identity === null) {
    return part === "label" ? name : "";
  }
  return renderKey(preferences, identity);
};
const token = (
  preferences: Preferences,
  value: string,
  definitions: PresentationDefinitions = productMetadata.catalog,
): string => {
  const key = `token.${value}`;
  if (hasMessage(key)) {
    return renderKey(preferences, key);
  }
  return metadata(
    preferences,
    definitions.entries.some((item) => item.subject === "AUTHORITY" && item.kind === value)
      ? "authority"
      : "act",
    value,
    "label",
    definitions,
  );
};
const fieldValueText = (
  preferences: Preferences,
  value: string | null,
  field: FieldCatalogEntry,
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
const hint = (preferences: Preferences, field: FieldCatalogEntry): string => {
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
        maximum: exactInteger(String(s.maximumCharacters ?? ""), preferences.displayLocale),
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
      : `${metadata(preferences, "group", group, "label", definitions)} · ${translate(preferences, "ui.action")}`;
  },
  fieldLabel: (name: string) => metadata(preferences, "field", name, "label", definitions),
  fieldMeaning: (name: string) => metadata(preferences, "field", name, "meaning", definitions),
  authorityLabel: (name: string) => metadata(preferences, "authority", name, "label", definitions),
  authorityMeaning: (name: string) =>
    metadata(preferences, "authority", name, "meaning", definitions),
  actMeaning: (name: string) => metadata(preferences, "act", name, "meaning", definitions),
  groupLabel: (name: string) => metadata(preferences, "group", name, "label", definitions),
  groupMeaning: (name: string) => metadata(preferences, "group", name, "meaning", definitions),
  actLabel: (name: string) => metadata(preferences, "act", name, "label", definitions),
  token: (value: string) => token(preferences, value, definitions),
  hint: (field: FieldCatalogEntry) => hint(preferences, field),
  integer: (value: string | number) => exactInteger(String(value), preferences.displayLocale),
  amount: (value: string) => exactAmount(value, preferences.displayLocale),
  date: (value: string) => calendarDate(value, preferences.displayLocale),
  fieldValue: (value: string | null, field: FieldCatalogEntry) =>
    fieldValueText(preferences, value, field),
});
