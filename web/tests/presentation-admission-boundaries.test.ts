import { expect, it } from "vitest";
import {
  loadPreferences,
  parsePreferences,
  resolveLanguage,
} from "../src/presentation/preferences";

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
  const json = '{"version":1,"language":"ar","displayLocale":"lv-LV"}';
  const bounded = json.padEnd(256, " ");
  expect(parsePreferences(bounded)).toEqual({ language: "ar", displayLocale: "lv-LV" });
  expect(parsePreferences(`${bounded} `)).toEqual({ language: "en", displayLocale: "en-GB" });
});
it("reads previously persisted version-one settings under their actual browser storage key", () => {
  localStorage.setItem(
    "claimcore.presentation.v1",
    '{"version":1,"language":"lv","displayLocale":"ar-EG"}',
  );
  expect(loadPreferences()).toEqual({ language: "lv", displayLocale: "ar-EG" });
});
