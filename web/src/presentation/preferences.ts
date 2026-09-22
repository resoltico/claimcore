export const languages = ["en", "lv", "ar", "en-XA"] as const;
export const displayLocales = ["en-GB", "lv-LV", "ar-EG"] as const;
export type Language = (typeof languages)[number];
export type DisplayLocale = (typeof displayLocales)[number];
export type Preferences = Readonly<{ language: Language; displayLocale: DisplayLocale }>;
export const defaults: Preferences = Object.freeze({ language: "en", displayLocale: "en-GB" });
export const preferenceKey = "claimcore.presentation.v1";

const canonicalTag = (value: unknown): string | null => {
  if (typeof value !== "string" || value.length > 64 || value.length === 0) return null;
  try {
    return Intl.getCanonicalLocales(value)[0] ?? null;
  } catch {
    return null;
  }
};
export const resolveLanguage = (value: unknown): Language => {
  const tag = canonicalTag(value);
  if (tag === "en-XA") return tag;
  const base = tag?.split("-")[0];
  return base === "ar" || base === "lv" ? base : "en";
};
export const resolveDisplayLocale = (value: unknown): DisplayLocale => {
  const tag = canonicalTag(value);
  return displayLocales.find((locale) => locale === tag) ?? defaults.displayLocale;
};
export const parsePreferences = (text: string | null): Preferences => {
  if (text === null || text.length > 256) return defaults;
  try {
    const value: unknown = JSON.parse(text);
    if (typeof value !== "object" || value === null || Array.isArray(value)) return defaults;
    const keys = Object.keys(value).sort().join(",");
    if (keys !== "displayLocale,language,version" || !("version" in value) || value.version !== 1)
      return defaults;
    if (!("language" in value) || !("displayLocale" in value)) return defaults;
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
