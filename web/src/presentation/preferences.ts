import {
  presentationLanguages,
  presentationDisplayLocales,
  presentationDefaults,
  presentationLanguageOptions,
  presentationPseudolanguage,
  presentationAccents,
  presentationModes,
  presentationLanguageAliases,
  presentationDisplayOptions,
} from "@contracts/client";
import tokens from "../theme/tokens.json" with { type: "json" };

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
  accent: accents.find((accent) => accent === tokens.$extensions.claimcore.defaultAccent)!,
});
const preferenceVersion = 3;
const keyPrefix = "claimcore.presentation.v";
export const preferenceKey = `${keyPrefix}${preferenceVersion}`;
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
const matchingLanguage = (tag: string): Language | undefined => {
  const aliases: Readonly<Record<string, Language>> = presentationLanguageAliases;
  let candidate = tag;
  while (candidate.length > 0) {
    const range = candidate;
    const exact = languages.find((language) => language === range);
    if (exact !== undefined) {
      return exact;
    }
    if (Object.hasOwn(aliases, candidate)) {
      return aliases[candidate];
    }
    const separator = candidate.lastIndexOf("-");
    candidate = separator < 0 ? "" : candidate.slice(0, separator);
  }
  return undefined;
};
export const resolveLanguage = (value: unknown): Language => {
  const tag = canonicalTag(value);
  return (tag === null ? undefined : matchingLanguage(tag)) ?? defaults.language;
};
export const resolveDisplayLocale = (value: unknown): DisplayLocale => {
  const tag = canonicalTag(value);
  return displayLocales.find((locale) => locale === tag) ?? defaults.displayLocale;
};
export const negotiatePreferences = (requested: ReadonlyArray<string>): Preferences => {
  const tags = requested.map(canonicalTag).filter((tag) => tag !== null);
  const language = tags
    .map(matchingLanguage)
    .find((candidate) => candidate !== undefined && candidate !== presentationPseudolanguage);
  const displayLocale = tags
    .map(
      (tag) =>
        displayLocales.find((locale) => locale === tag) ??
        presentationDisplayOptions.find((option) => option.language === matchingLanguage(tag))
          ?.value ??
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
export const parsePreferences = (text: string | null): Preferences | null => {
  if (text === null || text.length > 256) {
    return null;
  }
  try {
    const value: unknown = JSON.parse(text);
    if (!isStoredPreferences(value) || value.version !== preferenceVersion) {
      return null;
    }
    const language = languages.find((candidate) => candidate === value.language);
    const displayLocale = displayLocales.find((candidate) => candidate === value.displayLocale);
    const mode = modes.find((candidate) => candidate === value.mode);
    const accent = accents.find((candidate) => candidate === value.accent);
    return language === undefined ||
      displayLocale === undefined ||
      mode === undefined ||
      accent === undefined
      ? null
      : { language, displayLocale, mode, accent };
  } catch {
    return null;
  }
};

type IncompatiblePreferences = {
  kind: "incompatible";
  records: ReadonlyMap<string, string>;
};
export type PreferencePersistence =
  { kind: "available" } | { kind: "unavailable" } | IncompatiblePreferences;

const readStoredPreferences = ():
  | IncompatiblePreferences
  | {
      kind: "available";
      preferences: Preferences | null;
    } => {
  const raw = localStorage.getItem(preferenceKey);
  const current = parsePreferences(raw);
  if (current !== null) {
    return { kind: "available", preferences: current };
  }

  const incompatible = new Map<string, string>();
  if (raw !== null) {
    incompatible.set(preferenceKey, raw);
  }
  // Retired keys are recognized as evidence of stored preferences. They are never decoded.
  for (let index = 0; index < localStorage.length; index += 1) {
    const key = localStorage.key(index);
    if (
      key !== null &&
      key.startsWith(keyPrefix) &&
      key !== preferenceKey &&
      /^[1-9][0-9]*$/u.test(key.slice(keyPrefix.length))
    ) {
      const retired = localStorage.getItem(key);
      if (retired !== null) {
        incompatible.set(key, retired);
      }
    }
  }
  return incompatible.size === 0
    ? { kind: "available", preferences: null }
    : { kind: "incompatible", records: incompatible };
};

export const loadPreferenceState = (): Readonly<{
  preferences: Preferences;
  detected: boolean;
  persistence: PreferencePersistence;
}> => {
  const fallback = negotiatePreferences(
    typeof navigator === "undefined" ? [] : navigator.languages,
  );
  try {
    const stored = readStoredPreferences();
    if (stored.kind === "available" && stored.preferences !== null) {
      return {
        preferences: stored.preferences,
        detected: false,
        persistence: { kind: "available" },
      };
    }
    return {
      preferences: fallback,
      detected: true,
      persistence: stored.kind === "incompatible" ? stored : { kind: "available" },
    };
  } catch {
    return { preferences: fallback, detected: true, persistence: { kind: "unavailable" } };
  }
};
const writePreferences = (value: Preferences): PreferencePersistence => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: preferenceVersion,
      language: value.language,
      displayLocale: value.displayLocale,
      mode: value.mode,
      accent: value.accent,
    }),
  );
  return { kind: "available" };
};
export const savePreferences = (value: Preferences): PreferencePersistence => {
  try {
    const stored = readStoredPreferences();
    return stored.kind === "incompatible" ? stored : writePreferences(value);
  } catch {
    return { kind: "unavailable" };
  }
};
export const resetPreferences = (
  value: Preferences,
  acknowledged: IncompatiblePreferences,
): PreferencePersistence => {
  try {
    const stored = readStoredPreferences();
    if (stored.kind === "available") {
      return { kind: "available" };
    }
    if (
      stored.records.size !== acknowledged.records.size ||
      [...stored.records].some(([key, raw]) => acknowledged.records.get(key) !== raw)
    ) {
      return stored;
    }

    // Replace the current key atomically before clearing confirmed retired entries.
    const written = writePreferences(value);
    for (const [key, raw] of stored.records) {
      if (key !== preferenceKey && localStorage.getItem(key) === raw) {
        localStorage.removeItem(key);
      }
    }
    return written;
  } catch {
    return { kind: "unavailable" };
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
