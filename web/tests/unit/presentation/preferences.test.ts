import { expect, it } from "vitest";
import {
  defaults,
  loadPreferences,
  parsePreferences,
  resolveLanguage,
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
  const json = '{"version":2,"language":"ar","displayLocale":"lv-LV","theme":"SYSTEM"}';
  const bounded = json.padEnd(256, " ");
  expect(parsePreferences(bounded)).toEqual({
    language: "ar",
    displayLocale: "lv-LV",
    theme: "SYSTEM" as const,
  });
  expect(parsePreferences(`${bounded} `)).toEqual({
    language: "en",
    displayLocale: "en-GB",
    theme: "SYSTEM" as const,
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
