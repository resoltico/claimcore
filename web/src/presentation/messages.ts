import IntlMessageFormat from "intl-messageformat";
import { type MessageFormatElement } from "@formatjs/icu-messageformat-parser";
import en from "./generated/en.json";
import lv from "./generated/lv.json";
import ar from "./generated/ar.json";
import pseudo from "./generated/en-XA.json";
import shapes from "./generated/arguments.json";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

const catalogs = { en, lv, ar, "en-XA": pseudo } as unknown as Record<
  string,
  Record<string, MessageFormatElement[]>
>;
const shapeTable: Readonly<Record<string, Readonly<Record<string, string>>>> = shapes;
const cache = new Map<string, IntlMessageFormat>();
const message = (key: string, preferences: Preferences): IntlMessageFormat => {
  const cacheKey = `${preferences.language}|${preferences.displayLocale}|${key}`;
  const cached = cache.get(cacheKey);
  if (cached !== undefined) return cached;
  const ast = catalogs[preferences.language]![key] ?? catalogs["en"]![key]!;
  const grammar = preferences.language === "en-XA" ? "en" : preferences.language;
  const formatter = new IntlMessageFormat(ast, grammar, undefined, {
    formatters: {
      getNumberFormat: () =>
        new Intl.NumberFormat(preferences.displayLocale, { maximumFractionDigits: 0 }),
      getDateTimeFormat: Intl.DateTimeFormat,
      getPluralRules: (_locales, options) => new Intl.PluralRules(grammar, options),
    },
  });
  cache.set(cacheKey, formatter);
  return formatter;
};
const safeArgs = (key: string, values: Readonly<Record<string, string | number>>): boolean => {
  const shape = Object.hasOwn(shapeTable, key) ? shapeTable[key] : undefined;
  if (
    shape === undefined ||
    Object.keys(values).sort().join(",") !== Object.keys(shape).sort().join(",")
  )
    return false;
  return Object.entries(shape).every(
    ([name, role]) =>
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
  if (!safeArgs(key, values))
    return String(message("notice.unknownDiagnostic", preferences).format());
  const prepared = Object.fromEntries(
    Object.entries(values).map(([k, v]) => [k, isolate(v, preferences.language === "ar")]),
  );
  return String(message(key, preferences).format(prepared));
};
export const translate = <K extends MessageKey>(
  preferences: Preferences,
  key: K,
  ...values: Values<K>
): string => renderKey(preferences, key, values[0] ?? {});
export const hasMessage = (key: string): key is MessageKey => Object.hasOwn(shapeTable, key);
