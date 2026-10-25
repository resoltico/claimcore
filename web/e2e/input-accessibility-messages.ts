import enAuthority from "../../src/ClaimCore.Contract/Localization/en.json" with { type: "json" };

import lvAuthority from "../../src/ClaimCore.Contract/Localization/lv.json" with { type: "json" };

import arAuthority from "../../src/ClaimCore.Contract/Localization/ar.json" with { type: "json" };

const messages = {
  en: { ...enAuthority },
  lv: { ...lvAuthority },
  ar: { ...arAuthority },
};

export const inputMessage = (
  language: keyof typeof messages,
  key: keyof typeof messages.en,
): string => messages[language][key];
