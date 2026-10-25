import { IntlMessageFormat } from "intl-messageformat";
import enAuthority from "../../src/ClaimCore.Contract/Localization/en.authority.json" with { type: "json" };
import enDiagnostic from "../../src/ClaimCore.Contract/Localization/en.diagnostic.json" with { type: "json" };
import enField from "../../src/ClaimCore.Contract/Localization/en.field.json" with { type: "json" };
import enGroup from "../../src/ClaimCore.Contract/Localization/en.group.json" with { type: "json" };
import lvAuthority from "../../src/ClaimCore.Contract/Localization/lv.authority.json" with { type: "json" };
import lvDiagnostic from "../../src/ClaimCore.Contract/Localization/lv.diagnostic.json" with { type: "json" };
import lvField from "../../src/ClaimCore.Contract/Localization/lv.field.json" with { type: "json" };
import lvGroup from "../../src/ClaimCore.Contract/Localization/lv.group.json" with { type: "json" };
import arAuthority from "../../src/ClaimCore.Contract/Localization/ar.authority.json" with { type: "json" };
import arDiagnostic from "../../src/ClaimCore.Contract/Localization/ar.diagnostic.json" with { type: "json" };
import arField from "../../src/ClaimCore.Contract/Localization/ar.field.json" with { type: "json" };
import arGroup from "../../src/ClaimCore.Contract/Localization/ar.group.json" with { type: "json" };

const messages = {
  en: { ...enAuthority, ...enDiagnostic, ...enField, ...enGroup },
  lv: { ...lvAuthority, ...lvDiagnostic, ...lvField, ...lvGroup },
  ar: { ...arAuthority, ...arDiagnostic, ...arField, ...arGroup },
};

export const inputMessage = (
  language: keyof typeof messages,
  key: keyof typeof messages.en,
): string => String(new IntlMessageFormat(messages[language][key], language).format());
