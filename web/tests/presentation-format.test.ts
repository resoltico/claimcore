import { expect, it } from "vitest";
import { calendarDate, displayField, exactAmount, exactInteger } from "../src/presentation/format";
import { createPresenter } from "../src/presentation/presenter";
import { defaults, displayLocales } from "../src/presentation/preferences";
import { definition } from "./v2-ui.fixtures";

it("preserves every permitted amount digit and trailing zero in independent display locales", () => {
  const value = "999999999999999999.0100";
  expect(exactAmount(value, "en-GB")).toBe("999,999,999,999,999,999.0100");
  expect(exactAmount(value, "lv-LV")).toBe("999 999 999 999 999 999,0100");
  expect(exactAmount(value, "ar-EG")).toBe("٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٫٠١٠٠");
  for (const locale of displayLocales) {
    expect(exactAmount("0", locale)).toBe(new Intl.NumberFormat(locale).format(0));
    expect(exactAmount("1.0000", locale)).not.toBe(exactAmount("1", locale));
    expect(exactAmount("0.0001", locale)).not.toBe(exactAmount("0", locale));
  }
});

it("leaves invalid authored decimal and integer text unchanged for core validation", () => {
  for (const value of [
    "",
    "01",
    "1,5",
    "١٢٫٣",
    "1.12345",
    "-1",
    "1e2",
    " 1",
    "1.",
    "1000000000000000000",
  ]) {
    expect(exactAmount(value, "ar-EG")).toBe(value);
  }
  for (const value of ["", "01", "-1", "1.5", "1e3", "10000000000000000000"])
    expect(exactInteger(value, "lv-LV")).toBe(value);
  expect(exactInteger("9223372036854775807", "en-GB")).toBe("9,223,372,036,854,775,807");
});

it("formats Gregorian date-only values without shifting days or truncating early years", () => {
  expect(calendarDate("0001-01-01", "en-GB")).toBe("01/01/0001");
  expect(calendarDate("0099-12-31", "lv-LV")).toBe("31.12.0099");
  expect(calendarDate("9999-12-31", "en-GB")).toBe("31/12/9999");
  expect(calendarDate("2024-02-29", "ar-EG")).toContain("٢٩");
  expect(calendarDate("2026-03-29", "en-GB")).toBe("29/03/2026");
  expect(calendarDate("2026-10-25", "en-GB")).toBe("25/10/2026");
  for (const value of [
    "",
    "0000-01-01",
    "2026-02-30",
    "2025-02-29",
    "2026-00-01",
    "2026-13-01",
    "2026-01-00",
    "1-01-01",
    "2026-01-01T00:00:00Z",
  ])
    expect(calendarDate(value, "ar-EG")).toBe(value);
});

it("formats only read-only scalar displays and leaves technical text and currency invariant", () => {
  const fields = definition.definition.fields;
  const field = (name: string) => fields.find((item) => item.name === name)!;
  expect(displayField("12.5000", field("claimedAmount"), "lv-LV")).toBe("12,5000");
  expect(displayField("2026-09-22", field("incidentDate"), "en-GB")).toBe("22/09/2026");
  expect(displayField("EUR", field("claimedCurrency"), "ar-EG")).toBe("EUR");
  const p = createPresenter({ language: "ar", displayLocale: "lv-LV" });
  expect(p.fieldValue(null, field("claimedAmount"))).not.toBe(
    p.fieldValue("0", field("claimedAmount")),
  );
  expect(p.fieldValue("OPENED", field("status"))).not.toBe("OPENED");
  expect(p.fieldValue("12.5000", field("claimedAmount"))).toBe("12,5000");
});

it("uses stable metadata identity rather than supplied English labels as catalog authority", () => {
  const p = createPresenter(defaults);
  expect(p.fieldLabel("claimantName")).toBe("Claimant name");
  expect(p.fieldMeaning("claimantName")).not.toBe("");
  expect(p.commandLabel("CLOSE")).toBe("Close the case");
  expect(p.commandMeaning("CLOSE")).not.toBe("");
  expect(p.groupLabel("registration")).not.toBe("");
  expect(p.groupMeaning("registration")).not.toBe("");
  expect(p.fieldLabel("FUTURE_TECHNICAL_FIELD")).toBe("FUTURE_TECHNICAL_FIELD");
  expect(p.fieldMeaning("FUTURE_TECHNICAL_FIELD")).toBe("");
  expect(p.token("CLOSE")).toBe(p.commandLabel("CLOSE"));
  expect(p.token("opaque-identifier")).toBe("opaque-identifier");
  expect(p.integer("42")).toBe("42");
  expect(p.amount("42.0000")).toBe("42.0000");
  expect(p.date("2026-09-22")).toBe("22/09/2026");
  for (const field of definition.definition.fields) expect(p.hint(field)).not.toBe("");
});
