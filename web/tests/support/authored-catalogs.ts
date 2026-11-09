import coreEnglish from "../../../src/ClaimCore.Presentation/Messages/Localization/en.json" with { type: "json" };
import coreLatvian from "../../../src/ClaimCore.Presentation/Messages/Localization/lv.json" with { type: "json" };
import coreArabic from "../../../src/ClaimCore.Presentation/Messages/Localization/ar.json" with { type: "json" };
import chromeEnglish from "../../locales/en.json" with { type: "json" };
import chromeLatvian from "../../locales/lv.json" with { type: "json" };
import chromeArabic from "../../locales/ar.json" with { type: "json" };

// Authored texts are renderer inputs. These fixtures do not resolve or emulate source-hash fallback.
const texts = (input: Readonly<Record<string, { text: string; source: string }>>) =>
  Object.fromEntries(Object.entries(input).map(([key, value]) => [key, value.text]));
export const coreCatalogs = { en: coreEnglish, lv: texts(coreLatvian), ar: texts(coreArabic) };
export const chromeCatalogs = {
  en: chromeEnglish,
  lv: texts(chromeLatvian),
  ar: texts(chromeArabic),
};
