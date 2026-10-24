import IntlMessageFormat from "intl-messageformat";
import { parse, type MessageFormatElement } from "@formatjs/icu-messageformat-parser";
import { catalogs as generatedCatalogs } from "./generated/catalogs";
import { presentationPseudolanguage, presentationDefaults } from "@contracts/client";
import { languageDirection } from "./preferences";
import { pseudolocalize } from "./pseudolocalization.mjs";
import shapes from "./generated/arguments.json";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

const catalogs: Readonly<Record<string, Readonly<Record<string, string>>>> = generatedCatalogs;
const shapeTable: Readonly<Record<string, Readonly<Record<string, string>>>> = shapes;
const cache = new Map<string, IntlMessageFormat>();
const message = (key: string, preferences: Preferences): IntlMessageFormat => {
  const cacheKey = `${preferences.language}|${key}`;
  const cached = cache.get(cacheKey);
  if (cached !== undefined) {
    return cached;
  }
  const ast =
    preferences.language === presentationPseudolanguage
      ? ([
          { type: 0, value: "⟦" },
          ...pseudolocalize(parse(catalogs[presentationDefaults.language]![key]!)),
          { type: 0, value: "⟧" },
        ] satisfies MessageFormatElement[])
      : parse(catalogs[preferences.language]![key]!);
  const grammar =
    preferences.language === presentationPseudolanguage
      ? presentationDefaults.language
      : preferences.language;
  const formatter = new IntlMessageFormat(ast, grammar);
  cache.set(cacheKey, formatter);
  return formatter;
};
const safeArgs = (key: string, values: Readonly<Record<string, string | number>>): boolean => {
  const shape = Object.hasOwn(shapeTable, key) ? shapeTable[key] : undefined;
  if (shape === undefined || Object.keys(values).length !== Object.keys(shape).length) {
    return false;
  }
  return Object.entries(shape).every(
    ([name, role]) =>
      Object.hasOwn(values, name) &&
      typeof values[name] === role &&
      (role !== "number" || (Number.isSafeInteger(values[name]) && Number(values[name]) >= 0)),
  );
};
const isolate = (value: string | number, rtl: boolean): string | number =>
  rtl && typeof value === "string" ? `\u2068${value}\u2069` : value;
export const renderKey = (
  preferences: Preferences,
  key: string,
  values: Readonly<Record<string, string | number>> = {},
): string => {
  if (!safeArgs(key, values)) {
    return String(message("notice.unknownDiagnostic", preferences).format());
  }
  const prepared = Object.fromEntries(
    Object.entries(values).map(([k, v]) => [
      k,
      isolate(v, languageDirection(preferences.language) === "rtl"),
    ]),
  );
  return String(message(key, preferences).format(prepared));
};
export const translate = <K extends MessageKey>(
  preferences: Preferences,
  key: K,
  ...values: Values<K>
): string => renderKey(preferences, key, values[0] ?? {});
export const hasMessage = (key: string): key is MessageKey => Object.hasOwn(shapeTable, key);

/** Replace a string argument with a DOM-isolated value, retaining ICU word order. */
export const renderIsolatedValue = <T>(
  preferences: Preferences,
  key: string,
  values: Readonly<Record<string, string | number>>,
  reference: T,
  argument = "reference",
): (string | T)[] => {
  if (!safeArgs(key, values) || shapeTable[key]?.[argument] !== "string") {
    return [renderKey(preferences, "notice.unknownDiagnostic")];
  }
  const prepared = Object.fromEntries(
    Object.entries(values).map(([k, v]) => [
      k,
      isolate(v, languageDirection(preferences.language) === "rtl"),
    ]),
  );
  return message(key, preferences)
    .formatToParts<T>({ ...prepared, [argument]: reference })
    .map((part) => part.value);
};
