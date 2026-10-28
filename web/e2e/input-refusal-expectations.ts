import { expect, type Page, type Locator } from "@playwright/test";

// Independent localized meaning and summary samples; specific issue samples remain with each scenario.
export const issueMeanings = {
  en: {
    incidentDate: "Date on which the incident occurred.",
    claimedAmount: "Amount requested by the claimant.",
    claimedCurrency: "Currency in which the amount is claimed.",
    claimantName: "Name of the person or company making the claim.",
  },
  lv: {
    incidentDate: "Datums, kurā notika negadījums.",
    claimedAmount: "Prasītāja pieprasītā summa.",
    claimedCurrency: "Valūta, kurā summa pieprasīta.",
    claimantName: "Prasījumu iesniegušās personas vārds vai uzņēmuma nosaukums.",
  },
  ar: {
    incidentDate: "التاريخ الذي وقعت فيه الواقعة.",
    claimedAmount: "المبلغ الذي يطلبه المطالب.",
    claimedCurrency: "العملة التي يُطالب بها المبلغ.",
    claimantName: "اسم الشخص أو الشركة التي تقدّم المطالبة.",
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
  await expect(page.getByRole("alert")).toHaveText(summaries[language]);
  await expect(control).toHaveAccessibleDescription(
    new RegExp(`${literal(expected.meaning)}[\\s\\S]*${literal(expected.issue)}`, "u"),
  );
  await expect(control).toHaveAttribute("aria-invalid", "true");
};
