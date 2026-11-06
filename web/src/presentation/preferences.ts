import {
  presentationLanguages,
  presentationDisplayLocales,
  presentationDefaults,
  presentationLanguageOptions,
  presentationPseudolanguage,
  presentationAccents,
  presentationModes,
} from "@contracts/client";
export const languages = presentationLanguages;
export const displayLocales = presentationDisplayLocales;
export const accents = presentationAccents;
export const modes = presentationModes;
export type Language = (typeof languages)[number];
export type DisplayLocale = (typeof displayLocales)[number];
type Accent = (typeof accents)[number];
type Mode = (typeof modes)[number];
export type Preferences = Readonly<{
  language: Language;
  displayLocale: DisplayLocale;
  mode: Mode;
  accent: Accent;
}>;
export type PreferenceChange =
  | Readonly<{ kind: "LANGUAGE"; language: Language }>
  | Readonly<{ kind: "DISPLAY_LOCALE"; displayLocale: DisplayLocale }>
  | Readonly<{ kind: "MODE"; mode: Mode }>
  | Readonly<{ kind: "ACCENT"; accent: Accent }>;
export const changePreferences = (current: Preferences, change: PreferenceChange): Preferences => {
  switch (change.kind) {
    case "LANGUAGE":
      return { ...current, language: change.language };
    case "DISPLAY_LOCALE":
      return { ...current, displayLocale: change.displayLocale };
    case "MODE":
      return { ...current, mode: change.mode };
    case "ACCENT":
      return { ...current, accent: change.accent };
  }
};
export const defaults: Preferences = Object.freeze({
  ...presentationDefaults,
  mode: "system",
  accent: "green",
});
export const preferenceKey = "claimcore.presentation.v3";
const resolveMode = (value: unknown): Mode => modes.find((mode) => mode === value) ?? defaults.mode;
const resolveAccent = (value: unknown): Accent =>
  accents.find((accent) => accent === value) ?? defaults.accent;
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
export const negotiatePreferences = (requested: ReadonlyArray<string>): Preferences => {
  const tags = requested.map(canonicalTag).filter((tag) => tag !== null);
  const language = tags
    .map((tag) => tag.split("-")[0])
    .find((base) => languages.some((candidate) => candidate === base));
  const displayLocale = tags
    .map(
      (tag) =>
        displayLocales.find((locale) => locale === tag) ??
        displayLocales.find((locale) => locale.split("-")[0] === tag.split("-")[0]),
    )
    .find((locale) => locale !== undefined);
  return {
    ...defaults,
    language: resolveLanguage(language),
    displayLocale: displayLocale ?? defaults.displayLocale,
  };
};
type StoredPreferences = Readonly<{
  language: unknown;
  displayLocale: unknown;
  version: unknown;
  mode: unknown;
  accent: unknown;
}>;
const isStoredPreferences = (value: unknown): value is StoredPreferences =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(",") === "accent,displayLocale,language,mode,version";
export const parsePreferences = (
  text: string | null,
  fallback: Preferences = defaults,
): Preferences => {
  if (text === null || text.length > 256) {
    return fallback;
  }
  try {
    const value: unknown = JSON.parse(text);
    if (!isStoredPreferences(value) || value.version !== 3) {
      return fallback;
    }
    return {
      language: resolveLanguage(value.language),
      displayLocale: resolveDisplayLocale(value.displayLocale),
      mode: resolveMode(value.mode),
      accent: resolveAccent(value.accent),
    };
  } catch {
    return fallback;
  }
};
export const loadPreferenceState = (): Readonly<{
  preferences: Preferences;
  detected: boolean;
}> => {
  const fallback = negotiatePreferences(
    typeof navigator === "undefined" ? [] : navigator.languages,
  );
  try {
    const preferences = parsePreferences(localStorage.getItem(preferenceKey), fallback);
    return { preferences, detected: preferences === fallback };
  } catch {
    return { preferences: fallback, detected: true };
  }
};
export const loadPreferences = (): Preferences => loadPreferenceState().preferences;
export const savePreferences = (value: Preferences): boolean => {
  try {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language: value.language,
        displayLocale: value.displayLocale,
        mode: value.mode,
        accent: value.accent,
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
