export const languages = ["en", "lv", "ar", "en-XA"] as const;
export const displayLocales = ["en-GB", "lv-LV", "ar-EG"] as const;
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
export const defaults: Preferences = Object.freeze({ language: "en", displayLocale: "en-GB" });
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
  if (tag === "en-XA") {
    return tag;
  }
  const base = tag?.split("-")[0];
  return base === "ar" || base === "lv" ? base : "en";
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
    localStorage.setItem(preferenceKey, JSON.stringify({ version: 1, ...value }));
    return true;
  } catch {
    return false;
  }
};
