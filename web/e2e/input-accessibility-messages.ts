import type { MessageKey } from "../src/presentation/types";

// Handwritten accessible-label and guidance expectations, independent of the browser renderer.
const samples: Record<"en" | "lv" | "ar", Readonly<Record<string, string>>> = {
  en: {
    "field.principal.issuer.label": "Identity issuer",
    "field.principal.issuer.hint": "The sign-in provider for this account.",
    "field.principal.subject.label": "Subject or client ID",
    "field.principal.subject.hint": "The account identifier from the sign-in provider.",
    "diagnostic.PRINCIPAL_FORMAT":
      "Use a canonical HTTPS issuer and a valid subject for the principal kind.",
    "diagnostic.TEXT_REQUIRED": "A non-blank value is required.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "Replacing a paid decision requires explicit payment reaffirmation or clearing the payment record.",
    "field.caseReference.label": "Case reference",
    "field.caseReference.hint":
      "Your own reference for this case, for example CLM-2026-0001. It cannot be changed later.",
    "group.registration.hint": "Choose which details to change.",
    "group.decision.hint": "Choose which details to change.",
    "group.payment.hint": "Choose which details to change.",
  },
  lv: {
    "field.principal.issuer.label": "Identitātes izdevējs",
    "field.principal.issuer.hint": "Šī konta pieteikšanās pakalpojums.",
    "field.principal.subject.label": "Subjekta vai klienta ID",
    "field.principal.subject.hint": "Konta identifikators no pieteikšanās pakalpojuma.",
    "diagnostic.PRINCIPAL_FORMAT":
      "Izmantojiet kanonisku HTTPS izdevēju un šim identitātes veidam derīgu subjektu.",
    "diagnostic.TEXT_REQUIRED": "Nepieciešama netukša vērtība.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "Apmaksāta lēmuma aizstāšanai skaidri jāapstiprina maksājums vai jādzēš tā ieraksts.",
    "field.caseReference.label": "Administratora lietas numurs",
    "field.caseReference.hint":
      "Jūsu atsauce šai lietai, piemēram, CLM-2026-0001. Vēlāk to nevar mainīt.",
    "group.registration.hint": "Izvēlieties maināmo informāciju.",
    "group.decision.hint": "Izvēlieties maināmo informāciju.",
    "group.payment.hint": "Izvēlieties maināmo informāciju.",
  },
  ar: {
    "field.principal.issuer.label": "مُصدر الهوية",
    "field.principal.issuer.hint": "موفر تسجيل الدخول لهذا الحساب.",
    "field.principal.subject.label": "معرّف المستخدم أو العميل",
    "field.principal.subject.hint": "معرّف الحساب لدى موفر تسجيل الدخول.",
    "diagnostic.PRINCIPAL_FORMAT":
      "استخدم جهة إصدار HTTPS معيارية ومعرّف موضوع صالحًا لنوع الهوية.",
    "diagnostic.TEXT_REQUIRED": "يجب إدخال قيمة غير فارغة.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "يتطلب استبدال قرار مدفوع إعادة تأكيد الدفع صراحةً أو مسح سجل الدفع.",
    "field.caseReference.label": "مرجع الملف لدى المعالج",
    "field.caseReference.hint": "مرجعك لهذا الملف، مثل CLM-2026-0001. لا يمكن تغييره لاحقًا.",
    "group.registration.hint": "اختر التفاصيل التي تريد تغييرها.",
    "group.decision.hint": "اختر التفاصيل التي تريد تغييرها.",
    "group.payment.hint": "اختر التفاصيل التي تريد تغييرها.",
  },
};
export const inputMessage = (language: keyof typeof samples, key: MessageKey): string => {
  const sample = samples[language][key];
  if (sample === undefined) {
    throw new Error("An independent accessible-message sample is required.");
  }
  return sample;
};
