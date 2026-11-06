import { presentationDisplayLocales } from "@contracts/client";

type LocaleNumbers = Readonly<{
  integer: Intl.NumberFormat;
  digits: ReadonlyArray<string>;
  digitValues: ReadonlyMap<string, string>;
  decimal: string;
  group: string | undefined;
  numberingSystem: string;
}>;

const supported = new Set<string>(presentationDisplayLocales);
const profiles = new Map<string, LocaleNumbers>();

const create = (locale: string): LocaleNumbers => {
  const integer = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 });
  const parts = new Intl.NumberFormat(locale, { useGrouping: true }).formatToParts(1234567.5);
  const digitFormat = new Intl.NumberFormat(locale, { useGrouping: false });
  const digits = Object.freeze(Array.from({ length: 10 }, (_, digit) => digitFormat.format(digit)));
  return Object.freeze({
    integer,
    digits,
    digitValues: new Map(digits.map((value, digit) => [value, String(digit)])),
    decimal: parts.find((part) => part.type === "decimal")?.value ?? ".",
    group: parts.find((part) => part.type === "group")?.value,
    numberingSystem: integer.resolvedOptions().numberingSystem,
  });
};

/** Retain only curated formats; other legitimate Intl helper inputs remain uncached. */
export const localeNumbers = (locale: string): LocaleNumbers => {
  const existing = profiles.get(locale);
  if (existing !== undefined) {
    return existing;
  }
  const profile = create(locale);
  if (supported.has(locale)) {
    profiles.set(locale, profile);
  }
  return profile;
};
