import { readCatalog } from "../api/catalog";
import { messageShapes as coreShapes } from "@contracts/messages";
import { chromeShapes } from "./chromeMessages";
import { presentationDefaults, presentationPseudolanguage } from "@contracts/client";
import { isObject } from "../protocol/client";
import { admitCatalog } from "./catalogAdmission";
import type { Language } from "./preferences";

export type CatalogLanguage = Exclude<Language, typeof presentationPseudolanguage>;
export type TextCatalog = Readonly<Record<string, string>>;
const installed = new Map<CatalogLanguage, TextCatalog>();
export const catalogLanguage = (language: Language): CatalogLanguage =>
  language === presentationPseudolanguage ? presentationDefaults.language : language;
/** Both owners are admitted before a pair can become available to a presenter. */
export const installLanguage = (
  language: CatalogLanguage,
  core: unknown,
  chrome: unknown,
): TextCatalog => {
  const complete = Object.freeze({
    ...admitCatalog(core, coreShapes),
    ...admitCatalog(chrome, chromeShapes),
  });
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
export const loadLanguage = async (language: Language): Promise<void> => {
  if (languageLoaded(language)) {
    return;
  }
  const selected = catalogLanguage(language);
  const catalog: unknown = await readCatalog(selected);
  if (!isObject(catalog)) {
    throw new Error("Invalid language catalog");
  }
  installLanguage(selected, catalog["core"], catalog["chrome"]);
};
