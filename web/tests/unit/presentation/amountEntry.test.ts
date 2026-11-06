import { presentationDisplayLocales } from "@contracts/client";
import { expect, it } from "vitest";
import { normalizeAmountEntry } from "../../../src/presentation/amountEntry";
it.each([
  ["1,234.50", "en-GB", "1234.50"],
  ["1,234.50", "en-US", "1234.50"],
  ["1,234.50", "ar-AE", "1234.50"],
  ["1.234,50", "de-DE", "1234.50"],
  ["1.234", "de-DE", "1234"],
  ["12.34,50", "de-DE", "12.34,50"],
  ["1\u202f234,50", "fr-FR", "1234.50"],
  ["1\u00a0234,50", "lv-LV", "1234.50"],
  ["١٬٢٣٤٫٥٠", "ar-EG", "1234.50"],
  ["1.0000", "lv-LV", "1.0000"],
  ["1,23.45", "en-GB", "1,23.45"],
  [" 12", "en-GB", " 12"],
  ["1,234.12345", "en-GB", "1,234.12345"],
  ["12,345.6700", "ko-KR", "12345.6700"],
  ["12,345.6700", "zh-CN", "12345.6700"],
  ["12,345.6700", "zh-TW", "12345.6700"],
  ["12,345.6700", "zh-HK", "12345.6700"],
  ["12,345.6700", "ja-JP", "12345.6700"],
  ["12.345,6700", "es-ES", "12345.6700"],
  ["12,345.6700", "es-MX", "12345.6700"],
  ["12.345,6700", "es-AR", "12345.6700"],
  ["12\u00a0345,6700", "de-AT", "12345.6700"],
  ["12'345.6700", "de-CH", "12345.6700"],
  ["999'999'999'999'999'999.0100", "de-CH", "999999999999999999.0100"],
  ["0,0001", "es-AR", "0.0001"],
  ["12'34.50", "de-CH", "12'34.50"],
  ["12\u2019345.67", "de-CH", "12\u2019345.67"],
  ["12 345,67", "de-AT", "12 345,67"],
  ["12,345.6789", "es-ES", "12,345.6789"],
  ["12.345,67", "es-MX", "12.345,67"],
  ["12'345.67890", "de-CH", "12'345.67890"],
  ["+12,345.67", "ja-JP", "+12,345.67"],
  ["-12.345,67", "es-AR", "-12.345,67"],
  ["01,50", "de-AT", "01,50"],
  ["1e3", "ko-KR", "1e3"],
])("normalizes only unambiguous %s in %s", (raw, locale, expected) => {
  expect(normalizeAmountEntry(raw, locale)).toBe(expected);
});

it("covers the curated display-locale vocabulary", () => {
  expect([...presentationDisplayLocales]).toEqual([
    "en-GB",
    "en-US",
    "lv-LV",
    "ar-EG",
    "ar-AE",
    "de-DE",
    "fr-FR",
    "ko-KR",
    "zh-CN",
    "zh-TW",
    "zh-HK",
    "ja-JP",
    "es-ES",
    "es-MX",
    "es-AR",
    "de-AT",
    "de-CH",
  ]);
});
