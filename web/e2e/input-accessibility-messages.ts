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
      "Keep registration facts or update selected facts. Omitted facts stay unchanged; amount and currency are asserted together.",
    "group.decision.meaning":
      "Keep the decision, update selected facts, or clear it. Amount and currency must be asserted together.",
    "group.payment.meaning":
      "Keep, replace, reaffirm or clear the recorded payment date. Reaffirmation retains the date; clearing it does not refund money.",
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
      "Saglabāt reģistrācijas datus vai atjaunināt izvēlētos datus. Nenorādītie dati paliek nemainīti; summa un valūta jānorāda kopā.",
    "group.decision.meaning":
      "Saglabāt lēmumu, atjaunināt izvēlētos datus vai dzēst lēmumu. Summa un valūta jānorāda kopā.",
    "group.payment.meaning":
      "Saglabāt, aizstāt, atkārtoti apstiprināt vai dzēst reģistrēto maksājuma datumu. Atkārtota apstiprināšana saglabā datumu; dzēšana neatmaksā naudu.",
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
    "group.registration.meaning":
      "إبقاء بيانات التسجيل أو تحديث البيانات المختارة. تبقى البيانات غير المحددة كما هي، ويجب إدخال المبلغ والعملة معًا.",
    "group.decision.meaning":
      "إبقاء القرار أو تحديث البيانات المختارة أو مسح القرار. يجب إدخال المبلغ والعملة معًا.",
    "group.payment.meaning":
      "إبقاء تاريخ الدفع المسجّل أو استبداله أو إعادة تأكيده أو مسحه. إعادة التأكيد تُبقي التاريخ، ومسحه لا يعيد الأموال.",
  },
};
export const inputMessage = (language: keyof typeof catalogs, key: MessageKey): string => {
  const sample = samples[language][key];
  if (sample === undefined) {
    throw new Error("An independent accessible-message sample is required.");
  }
  return sample;
};
