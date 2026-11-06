import { expect, it } from "vitest";
import {
  defaults,
  loadPreferences,
  parsePreferences,
  resolveLanguage,
  negotiatePreferences,
  resolveDisplayLocale,
} from "../../../src/presentation/preferences";

it("refuses nontext language values even when Intl could interpret an array-like object", () => {
  for (const value of [["lv"], ["ar-EG"], { 0: "lv", length: 1 }]) {
    expect(resolveLanguage(value)).toBe("en");
  }
});
it("admits the exact tag budget and refuses otherwise valid oversized tags", () => {
  const tag = `lv-x-${[...Array<string>(6).fill("abcdefgh"), "abcde"].join("-")}`;
  expect(tag).toHaveLength(64);
  expect(Intl.getCanonicalLocales(tag)).toHaveLength(1);
  expect(resolveLanguage(tag)).toBe("lv");
  expect(resolveLanguage(`${tag}f`)).toBe("en");
});
it("accepts exactly bounded settings and refuses valid JSON beyond its storage budget", () => {
  const json =
    '{"version":3,"language":"ar","displayLocale":"lv-LV","mode":"system","accent":"green"}';
  const bounded = json.padEnd(256, " ");
  expect(parsePreferences(bounded)).toEqual({
    language: "ar",
    displayLocale: "lv-LV",
    mode: "system" as const,
    accent: "green" as const,
  });
  expect(parsePreferences(`${bounded} `)).toEqual({
    language: "en",
    displayLocale: "en-GB",
    mode: "system" as const,
    accent: "green" as const,
  });
});
it("resets version-one settings and accepts only the current stored record", () => {
  localStorage.setItem(
    "claimcore.presentation.v1",
    '{"version":1,"language":"lv","displayLocale":"ar-EG"}',
  );
  expect(loadPreferences()).toEqual(defaults);
  expect(parsePreferences('{"version":1,"language":"lv","displayLocale":"ar-EG"}')).toEqual(
    defaults,
  );
});

it.each([
  ["ko-KR", "ko"],
  ["ja-JP", "ja"],
  ["es-MX", "es"],
  ["de-CH", "de"],
  ["zh", "zh-Hans"],
  ["zh-CN", "zh-Hans"],
  ["zh-SG", "zh-Hans"],
  ["zh-TW", "zh-Hant"],
  ["zh-HK", "zh-Hant"],
  ["zh-MO", "zh-Hant"],
  ["zh-Hant-CN", "zh-Hant"],
  ["zh-Hans-TW", "zh-Hans"],
])("resolves %s to %s without discarding script identity", (tag, language) => {
  expect(resolveLanguage(tag)).toBe(language);
  expect(negotiatePreferences(["it-IT", tag]).language).toBe(language);
});
it("keeps regional formatting separate from text and respects script in detected defaults", () => {
  expect(negotiatePreferences(["zh-Hant-CN"])).toMatchObject({
    language: "zh-Hant",
    displayLocale: "zh-TW",
  });
  expect(negotiatePreferences(["zh-HK"])).toMatchObject({
    language: "zh-Hant",
    displayLocale: "zh-HK",
  });
  expect(negotiatePreferences(["es-MX"])).toMatchObject({ language: "es", displayLocale: "es-MX" });
  expect(resolveDisplayLocale("de-CH")).toBe("de-CH");
  expect(negotiatePreferences(["fr-CA"])).toMatchObject({ language: "en", displayLocale: "fr-FR" });
  expect(
    parsePreferences(
      '{"version":3,"language":"ja","displayLocale":"de-CH","mode":"system","accent":"green"}',
    ),
  ).toMatchObject({ language: "ja", displayLocale: "de-CH" });
});
