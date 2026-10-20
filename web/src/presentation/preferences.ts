import {
  presentationLanguages,
  presentationDisplayLocales,
  presentationDefaults,
  presentationLanguageOptions,
  presentationPseudolanguage,
} from "@contracts/client";
export const languages = presentationLanguages;
export const displayLocales = presentationDisplayLocales;
export type Language = (typeof languages)[number];
export type DisplayLocale = (typeof displayLocales)[number];
export type Preferences = Readonly<{ language: Language; displayLocale: DisplayLocale }>;
export type PreferenceChange =
  | Readonly<{ kind: "LANGUAGE"; language: Language }>
  | Readonly<{ kind: "DISPLAY_LOCALE"; displayLocale: DisplayLocale }>;
export const changePreferences = (current: Preferences, change: PreferenceChange): Preferences =>
  change.kind === "LANGUAGE"
    ? { ...current, language: change.language }
    : { ...current, displayLocale: change.displayLocale };
export const defaults: Preferences = Object.freeze(presentationDefaults);
export const preferenceKey = "claimcore.presentation.v1";

const canonicalTag = (value: unknown): string | null => {
  if (typeof value !== "string" || value.length > 64) {
    return null;
  }
  try {
    return Intl.getCanonicalLocales(value)[0]!;
  } catch {
    return null;
  }
};
export const resolveLanguage = (value: unknown): Language => {
  const tag = canonicalTag(value);
  if (tag === presentationPseudolanguage) {
    return tag;
  }
  const base = tag?.split("-")[0];
  return languages.find((language) => language === base) ?? defaults.language;
};
export const resolveDisplayLocale = (value: unknown): DisplayLocale => {
  const tag = canonicalTag(value);
  return displayLocales.find((locale) => locale === tag) ?? defaults.displayLocale;
};
type StoredPreferences = Readonly<{ language: unknown; displayLocale: unknown; version: unknown }>;
const isStoredPreferences = (value: unknown): value is StoredPreferences =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(",") === "displayLocale,language,version";
export const parsePreferences = (text: string | null): Preferences => {
  if (text === null || text.length > 256) {
    return defaults;
  }
  try {
    const value: unknown = JSON.parse(text);
    if (!isStoredPreferences(value) || value.version !== 1) {
      return defaults;
    }
    return {
      language: resolveLanguage(value.language),
      displayLocale: resolveDisplayLocale(value.displayLocale),
    };
  } catch {
    return defaults;
  }
};
export const loadPreferences = (): Preferences => {
  try {
    return parsePreferences(localStorage.getItem(preferenceKey));
  } catch {
    return defaults;
  }
};
export const savePreferences = (value: Preferences): boolean => {
  try {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 1,
        language: value.language,
        displayLocale: value.displayLocale,
      }),
    );
    return true;
  } catch {
    return false;
  }
};

export const languageDirection = (language: Language): "ltr" | "rtl" => {
  const code = language === presentationPseudolanguage ? defaults.language : language;
  const option = presentationLanguageOptions.find((candidate) => candidate.value === code);
  if (option === undefined) {
    throw new Error("Unsupported core language direction.");
  }
  return option.direction;
};
