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
// Independent expected samples for Core-declared GUI input issue variants.
export const humanFormatIssues = {
  CALENDAR_DATE_REQUIRED: {
    en: "Enter a real date.",
    lv: "Ievadiet derīgu datumu.",
    ar: "أدخل تاريخًا صحيحًا.",
  },
  CURRENCY_FORMAT: {
    en: "Choose a supported currency code, for example EUR. Fund and non-national codes are not accepted.",
    lv: "Izvēlieties atbalstītu valūtas kodu, piemēram, EUR. Fondu un citi ar valstu valūtām nesaistīti kodi netiek pieņemti.",
    ar: "اختر رمز عملة مدعومًا، مثل EUR. لا تُقبل رموز الصناديق أو الرموز غير الوطنية.",
  },
} as const;
const summaries = {
  en: "Fields to check: 1.",
  lv: "Pārbaudāmie lauki: 1.",
  ar: "الحقول المطلوب التحقق منها: 1.",
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
  await expect(page.locator("[data-error-summary]").locator("bdi").first()).toHaveText("1");
  // Supplied arguments use semantic bdi isolation, whose control characters are absent from accessible text.
  const issueText = expected.issue.replace(/[\u2068\u2069]/gu, "");
  const isolated = [...expected.issue.matchAll(/\u2068([^\u2069]+)\u2069/gu)];
  if (isolated.length > 0) {
    const ids =
      (await control.getAttribute("aria-describedby"))?.split(/\s+/u).filter(Boolean) ?? [];
    expect(ids.length).toBeGreaterThan(0);
    const descriptions = page.locator(ids.map((id) => `[id="${id}"]`).join(","));
    for (const match of isolated) {
      const [, value] = match;
      if (value === undefined) {
        throw new Error("Expected isolated argument value");
      }
      await expect(
        descriptions.locator("bdi").filter({ hasText: new RegExp(`^${literal(value)}$`, "u") }),
      ).toHaveCount(1);
    }
  }
  await expect(control).toHaveAccessibleDescription(
    new RegExp(`${literal(expected.meaning)}[\\s\\S]*${literal(issueText)}`, "u"),
  );
  await expect(control).toHaveAttribute("aria-invalid", "true");
};
