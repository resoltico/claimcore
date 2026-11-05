import { expect, it } from "vitest";
import { calendarDate, calendarDateParts } from "../../src/presentation/format";
import { createPresenter } from "../../src/presentation/presenter";
import { defaults } from "../../src/presentation/preferences";
const cases = [
  {
    locale: "en-GB",
    text: "05/11/2026",
    parts: [
      ["day", "05"],
      ["literal", "/"],
      ["month", "11"],
      ["literal", "/"],
      ["year", "2026"],
    ],
  },
  {
    locale: "en-US",
    text: "11/05/2026",
    parts: [
      ["month", "11"],
      ["literal", "/"],
      ["day", "05"],
      ["literal", "/"],
      ["year", "2026"],
    ],
  },
  {
    locale: "lv-LV",
    text: "05.11.2026",
    parts: [
      ["day", "05"],
      ["literal", "."],
      ["month", "11"],
      ["literal", "."],
      ["year", "2026"],
    ],
  },
  {
    locale: "ar-EG",
    text: "٠٥\u200f/١١\u200f/٢٠٢٦",
    parts: [
      ["day", "٠٥"],
      ["literal", "\u200f/"],
      ["month", "١١"],
      ["literal", "\u200f/"],
      ["year", "٢٠٢٦"],
    ],
  },
  {
    locale: "ar-AE",
    text: "05\u200f/11\u200f/2026",
    parts: [
      ["day", "05"],
      ["literal", "\u200f/"],
      ["month", "11"],
      ["literal", "\u200f/"],
      ["year", "2026"],
    ],
  },
  {
    locale: "de-DE",
    text: "05.11.2026",
    parts: [
      ["day", "05"],
      ["literal", "."],
      ["month", "11"],
      ["literal", "."],
      ["year", "2026"],
    ],
  },
  {
    locale: "fr-FR",
    text: "05/11/2026",
    parts: [
      ["day", "05"],
      ["literal", "/"],
      ["month", "11"],
      ["literal", "/"],
      ["year", "2026"],
    ],
  },
] as const;
it.each(cases)(
  "preserves the literal numeric date and part order in $locale",
  ({ locale, text, parts }) => {
    const expected = parts.map(([type, value]) => ({ type, value }));
    expect(calendarDateParts("2026-11-05", locale)).toEqual(expected);
    expect(calendarDate("2026-11-05", locale)).toBe(text);
    const presenter = createPresenter({ ...defaults, displayLocale: locale });
    expect(presenter.dateParts("2026-11-05")).toEqual(expected);
    expect(presenter.date("2026-11-05")).toBe(text);
  },
);
it("keeps padded early years, Arabic digits and invalid text without inventing dates", () => {
  expect(calendarDateParts("0001-01-01", "ar-EG")).toEqual([
    { type: "day", value: "٠١" },
    { type: "literal", value: "\u200f/" },
    { type: "month", value: "٠١" },
    { type: "literal", value: "\u200f/" },
    { type: "year", value: "٠٠٠١" },
  ]);
  expect(calendarDate("0099-12-31", "lv-LV")).toBe("31.12.0099");
  expect(calendarDate("9999-12-31", "en-GB")).toBe("31/12/9999");
  for (const value of ["", "0000-01-01", "2025-02-29", "2026-02-30", "1-01-01"]) {
    expect(calendarDateParts(value, "ar-EG")).toEqual([{ type: "literal", value }]);
    expect(calendarDate(value, "ar-EG")).toBe(value);
  }
});
it("retains complete named heading strings including weekday and locale literals", () => {
  expect(calendarDate("2026-11-05", "en-GB", true)).toBe("Thu, 5 Nov 2026");
  expect(calendarDate("2026-11-05", "lv-LV", true)).toBe("ceturtd., 2026. g. 5. nov.");
  expect(calendarDate("2026-11-05", "ar-EG", true)).toBe("الخميس، ٥ نوفمبر ٢٠٢٦");
});
