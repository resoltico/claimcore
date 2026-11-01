import { messageShapes as coreShapes } from "@contracts/messages";
import { messageShapes as chromeShapes } from "../generated/chrome-messages";
import { presentationDefaults, presentationPseudolanguage } from "@contracts/client";
import { isObject } from "../protocol/client";
import { templateParts } from "./templates";
import type { Language } from "./preferences";

export type CatalogLanguage = Exclude<Language, typeof presentationPseudolanguage>;
export type TextCatalog = Readonly<Record<string, string>>;
const installed = new Map<CatalogLanguage, TextCatalog>();
export const catalogLanguage = (language: Language): CatalogLanguage =>
  language === presentationPseudolanguage ? presentationDefaults.language : language;
const admit = (
  raw: unknown,
  shapes: Readonly<Record<string, Readonly<Record<string, string>>>>,
): TextCatalog => {
  if (!isObject(raw)) {
    throw new Error("Invalid language catalog");
  }
  const values: Record<string, string> = {};
  for (const [key, shape] of Object.entries(shapes)) {
    const text = raw[key];
    if (typeof text !== "string") {
      throw new Error("Incomplete language catalog");
    }
    const names = new Set(
      templateParts(text)
        .filter((part) => part.kind !== "literal")
        .map((part) => part.value),
    );
    if (
      names.size !== Object.keys(shape).length ||
      Object.keys(shape).some((name) => !names.has(name))
    ) {
      throw new Error("Invalid language arguments");
    }
    values[key] = text;
  }
  return Object.freeze(values);
};
/** Both owners are admitted before a pair can become available to a presenter. */
export const installLanguage = (
  language: CatalogLanguage,
  core: unknown,
  chrome: unknown,
): TextCatalog => {
  const complete = Object.freeze({ ...admit(core, coreShapes), ...admit(chrome, chromeShapes) });
  const existing = installed.get(language);
  if (existing !== undefined) {
    return existing;
  }
  installed.set(language, complete);
  return complete;
};
export const languageLoaded = (language: Language): boolean =>
  installed.has(catalogLanguage(language));
export const catalogFor = (language: Language): TextCatalog => {
  const catalog = installed.get(catalogLanguage(language));
  if (catalog === undefined) {
    throw new Error("A complete active language is required");
  }
  return catalog;
};
const importPair = (language: CatalogLanguage) => {
  switch (language) {
    case "en":
      return Promise.all([
        import("../../../src/ClaimCore.Protocol/Messages/Localization/en.json"),
        import("../../locales/en.json"),
      ]);
    case "lv":
      return Promise.all([
        import("../../../src/ClaimCore.Protocol/Messages/Localization/lv.json"),
        import("../../locales/lv.json"),
      ]);
    case "ar":
      return Promise.all([
        import("../../../src/ClaimCore.Protocol/Messages/Localization/ar.json"),
        import("../../locales/ar.json"),
      ]);
  }
};
export const loadLanguage = async (language: Language): Promise<void> => {
  if (languageLoaded(language)) {
    return;
  }
  const selected = catalogLanguage(language);
  const [core, chrome] = await importPair(selected);
  installLanguage(selected, core.default, chrome.default);
};
