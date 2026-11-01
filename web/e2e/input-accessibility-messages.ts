import type { catalogs } from "../src/generated/core-catalogs";
import type { MessageKey } from "../src/presentation/types";

// Handwritten accessible-label and guidance expectations, independent of the browser renderer.
const samples: Record<keyof typeof catalogs, Readonly<Record<string, string>>> = {
  en: {
    "field.principal.issuer.label": "Identity issuer",
    "field.principal.issuer.meaning": "The exact HTTPS issuer of the target principal.",
    "field.principal.subject.label": "Subject or client ID",
    "field.principal.subject.meaning":
      "The immutable subject or client ID of the target principal.",
    "diagnostic.PRINCIPAL_FORMAT":
      "Use a canonical HTTPS issuer and a valid subject for the principal kind.",
    "diagnostic.TEXT_REQUIRED": "A non-blank value is required.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "Replacing a paid decision requires explicit payment reaffirmation or clearing the payment record.",
    "field.caseReference.label": "Handler's case reference",
    "field.caseReference.meaning":
      "Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained.",
    "group.registration.meaning":
      "Keep the current registration details or replace all seven details together.",
    "group.decision.meaning":
      "Keep the current payment decision, replace its date, amount and currency together, or clear the decision.",
    "group.payment.meaning":
      "Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money.",
  },
  lv: {
    "field.principal.issuer.label": "Identitātes izdevējs",
    "field.principal.issuer.meaning": "Precīzs mērķa identitātes HTTPS izdevējs.",
    "field.principal.subject.label": "Subjekta vai klienta ID",
    "field.principal.subject.meaning":
      "Mērķa identitātes nemainīgais subjekta vai klienta identifikators.",
    "diagnostic.PRINCIPAL_FORMAT":
      "Izmantojiet kanonisku HTTPS izdevēju un šim identitātes veidam derīgu subjektu.",
    "diagnostic.TEXT_REQUIRED": "Nepieciešama netukša vērtība.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "Apmaksāta lēmuma aizstāšanai skaidri jāapstiprina maksājums vai jādzēš tā ieraksts.",
    "field.caseReference.label": "Administratora lietas numurs",
    "field.caseReference.meaning":
      "Numurs, ko piešķīris šī reģistra atlīdzību administrators, nevis apdrošinātājs. Drukājamas ASCII rakstzīmes bez atstarpēm; vienā instalācijā unikāls un nemaināms, burtu reģistrs identitāti nemaina, sākotnējā rakstība tiek saglabāta.",
    "group.registration.meaning":
      "Saglabāt pašreizējos reģistrācijas datus vai aizstāt visus septiņus datus kopā.",
    "group.decision.meaning":
      "Saglabāt pašreizējo izmaksas lēmumu, kopā aizstāt tā datumu, summu un valūtu vai dzēst lēmumu.",
    "group.payment.meaning":
      "Saglabāt pašreizējo maksājuma datumu, aizstāt to vai dzēst reģistrēto datumu. Datuma dzēšana neatmaksā naudu.",
  },
  ar: {
    "field.principal.issuer.label": "مُصدر الهوية",
    "field.principal.issuer.meaning": "مُصدر HTTPS المطابق لهوية الجهة المستهدفة.",
    "field.principal.subject.label": "معرّف المستخدم أو العميل",
    "field.principal.subject.meaning": "معرّف الموضوع أو العميل الثابت لهوية الجهة المستهدفة.",
    "diagnostic.PRINCIPAL_FORMAT":
      "استخدم جهة إصدار HTTPS معيارية ومعرّف موضوع صالحًا لنوع الهوية.",
    "diagnostic.TEXT_REQUIRED": "يجب إدخال قيمة غير فارغة.",
    "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED":
      "يتطلب استبدال قرار مدفوع إعادة تأكيد الدفع صراحةً أو مسح سجل الدفع.",
    "field.caseReference.label": "مرجع الملف لدى المعالج",
    "field.caseReference.meaning":
      "مرجع يحدده معالج المطالبة الذي يدير هذا السجل، وليس شركة التأمين. يستخدم محارف ASCII قابلة للطباعة دون مسافات، وهو فريد وثابت داخل التثبيت، ولا تغير حالة الأحرف هويته وتُحفظ كتابته الأصلية.",
    "group.registration.meaning": "إبقاء تفاصيل التسجيل الحالية أو استبدال التفاصيل السبعة معًا.",
    "group.decision.meaning":
      "إبقاء قرار الدفع الحالي أو استبدال تاريخه ومبلغه وعملته معًا أو مسح القرار.",
    "group.payment.meaning":
      "إبقاء تاريخ الدفع الحالي أو استبداله أو مسح التاريخ المسجّل. مسح التاريخ لا يعيد الأموال.",
  },
};
export const inputMessage = (language: keyof typeof catalogs, key: MessageKey): string => {
  const sample = samples[language][key];
  if (sample === undefined) {
    throw new Error("An independent accessible-message sample is required.");
  }
  return sample;
};
