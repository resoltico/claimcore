import { expect, type Page, type Locator } from "@playwright/test";

// Independent GUI widget-hint and summary samples; specific issue samples remain with each scenario.
export const issueMeanings = {
  en: {
    incidentDate: "When the incident happened.",
    claimedAmount: "How much the claimant is asking for.",
    claimedCurrency: "The currency of the claimed amount.",
    claimantName: "The person or company making the claim.",
  },
  lv: {
    incidentDate: "Kad notika negadījums.",
    claimedAmount: "Summa, ko pieprasa prasītājs.",
    claimedCurrency: "Izvēlieties valūtu pieprasītajai summai.",
    claimantName: "Persona vai uzņēmums, kas iesniedz prasījumu.",
  },
  ar: {
    incidentDate: "متى وقعت الواقعة.",
    claimedAmount: "المبلغ الذي يطلبه المطالب.",
    claimedCurrency: "اختر العملة التي طُلب بها المبلغ.",
    claimantName: "الشخص أو الشركة التي تقدم المطالبة.",
  },
} as const;
// GUI variants deliberately differ from the wire-format rules retained by CLI/help.
export const guiFormatIssues = {
  CALENDAR_DATE_REQUIRED: {
    en: "Enter a real date.",
    lv: "Ievadiet derīgu datumu.",
    ar: "أدخل تاريخًا صحيحًا.",
  },
  CURRENCY_FORMAT: {
    en: "Enter a three-letter currency code, for example EUR.",
    lv: "Ievadiet trīs burtu valūtas kodu, piemēram, EUR.",
    ar: "أدخل رمز عملة من ثلاثة أحرف، مثل EUR.",
  },
} as const;
const summaries = {
  en: "Input problems: 1.",
  lv: "Ievades kļūdu skaits: 1.",
  ar: "عدد مشكلات الإدخال: \u20681\u2069.",
} as const;
const literal = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
export const expectInputRefusal = async (
  page: Page,
  control: Locator,
  language: keyof typeof summaries,
  expected: { meaning: string; issue: string },
) => {
  await expect(page.locator("[data-error-summary]")).toContainText(summaries[language]);
  await expect(page.locator("[data-error-summary]").getByRole("link")).toHaveCount(1);
  await expect(control).toHaveAccessibleDescription(
    new RegExp(`${literal(expected.meaning)}[\\s\\S]*${literal(expected.issue)}`, "u"),
  );
  await expect(control).toHaveAttribute("aria-invalid", "true");
};
