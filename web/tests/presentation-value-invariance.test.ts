import { expect, it } from "vitest";
import { createPresenter } from "../src/presentation/presenter";
import { createDraft } from "../src/domain/metadata";
import { freezeRequest } from "../src/domain/operationRequest";
import { definition, operationId } from "./v3-ui.fixtures";

const arabicDigits = (value: string) =>
  value.replace(/[0-9]/gu, (digit) => "٠١٢٣٤٥٦٧٨٩"[Number(digit)]!);
const amountOracle = (whole: string, fraction: string, locale: string) => {
  const groups = whole.replace(/\B(?=(?:[0-9]{3})+(?![0-9]))/gu, "|");
  if (locale === "ar-EG") {
    return arabicDigits(`${groups.replaceAll("|", "٬")}٫${fraction}`);
  }
  return locale === "lv-LV"
    ? `${groups.replaceAll("|", " ")},${fraction}`
    : `${groups.replaceAll("|", ",")}.${fraction}`;
};
const dateOracle = (year: string, month: string, day: string, locale: string) => {
  if (locale === "ar-EG") {
    return arabicDigits(`${day}\u200f/${month}\u200f/${year}`);
  }
  return locale === "lv-LV" ? `${day}.${month}.${year}` : `${day}/${month}/${year}`;
};

it("preserves canonical values and frozen request identity over 500 seeded display vectors [CC-WEB-001]", () => {
  let seed = 739101;
  const next = () => {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed;
  };
  const field = (name: string) => definition.definition.fields.find((item) => item.name === name)!;
  for (let index = 0; index < 500; index += 1) {
    const whole =
      String(1 + (next() % 9)) +
      String(next())
        .padStart(10, "0")
        .slice(0, next() % 10);
    const fraction = String(next() % 10000).padStart(4, "0");
    const year = String(1 + (next() % 9999)).padStart(4, "0");
    const month = String(1 + (next() % 12)).padStart(2, "0");
    const day = String(1 + (next() % 28)).padStart(2, "0");
    const reference = `CASE-${next()} A\u0308 العربية\u200D\u202E`;
    const values = {
      claimedAmount: `${whole}.${fraction}`,
      incidentDate: `${year}-${month}-${day}`,
    };
    const frozen = freezeRequest(createDraft(operationId, reference, "0", "OPEN", values));
    const bytes = JSON.stringify(frozen);
    for (const choice of [index % 12, (index + 5) % 12]) {
      const language = (["en", "lv", "ar", "en-XA"] as const)[choice % 4]!;
      const displayLocale = (["en-GB", "lv-LV", "ar-EG"] as const)[Math.floor(choice / 4)]!;
      const p = createPresenter({ language, displayLocale });
      expect(p.fieldValue(values.claimedAmount, field("claimedAmount"))).toBe(
        amountOracle(whole, fraction, displayLocale),
      );
      expect(p.fieldValue(values.incidentDate, field("incidentDate"))).toBe(
        dateOracle(year, month, day, displayLocale),
      );
      expect(p.fieldValue(reference, field("caseReference"))).toBe(reference);
    }
    expect(JSON.stringify(frozen)).toBe(bytes);
    expect(createDraft(operationId, reference, "0", "OPEN", values)).toEqual(frozen);
  }
});
