import { expect, it } from "vitest";
import { normalizeAmountEntry } from "../../../src/presentation/amountEntry";
it.each([
  ["1,234.50", "en-GB", "1234.50"],
  ["1\u00a0234,50", "lv-LV", "1234.50"],
  ["١٬٢٣٤٫٥٠", "ar-EG", "1234.50"],
  ["1.0000", "lv-LV", "1.0000"],
  ["1,23.45", "en-GB", "1,23.45"],
  [" 12", "en-GB", " 12"],
  ["1,234.12345", "en-GB", "1,234.12345"],
])("normalizes only unambiguous %s in %s", (raw, locale, expected) => {
  expect(normalizeAmountEntry(raw, locale)).toBe(expected);
});
