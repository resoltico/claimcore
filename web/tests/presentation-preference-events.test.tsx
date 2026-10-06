import { act, renderHook } from "@testing-library/react";
import { expect, it } from "vitest";
import { PresentationProvider } from "../src/presentation/PresentationProvider";
import { usePresentation } from "../src/presentation/context";
import {
  defaults,
  changePreferences,
  preferenceKey,
  type Preferences,
  type PreferenceChange,
} from "../src/presentation/preferences";

it("applies batched preference changes to current state even through a stale event handler", () => {
  const { result } = renderHook(usePresentation, { wrapper: PresentationProvider });
  const stale = result.current.setPreferences;
  act(() => {
    stale({ kind: "LANGUAGE", language: "ar" });
    stale({ kind: "DISPLAY_LOCALE", displayLocale: "lv-LV" });
  });
  expect(result.current.language).toBe("ar");
  expect(result.current.displayLocale).toBe("lv-LV");
  expect(JSON.parse(localStorage.getItem(preferenceKey)!)).toEqual({
    version: 1,
    language: "ar",
    displayLocale: "lv-LV",
  });
  act(() => {
    stale({ kind: "DISPLAY_LOCALE", displayLocale: "ar-EG" });
    stale({ kind: "LANGUAGE", language: "en" });
  });
  expect(result.current.language).toBe("en");
  expect(result.current.displayLocale).toBe("ar-EG");
  expect(document.documentElement.lang).toBe("en");
  expect(document.documentElement.dir).toBe("ltr");
});

it("preserves the independent preference through bounded reproducible event sequences", () => {
  let random = 739101;
  let actual: Preferences = defaults;
  let language: Preferences["language"] = "en";
  let displayLocale: Preferences["displayLocale"] = "en-GB";
  for (let index = 0; index < 500; index += 1) {
    random = (Math.imul(random, 1664525) + 1013904223) >>> 0;
    const change: PreferenceChange =
      (random & 1) === 0
        ? {
            kind: "LANGUAGE",
            language: (["en", "lv", "ar", "en-XA"] as const)[(random >>> 1) % 4]!,
          }
        : {
            kind: "DISPLAY_LOCALE",
            displayLocale: (["en-GB", "lv-LV", "ar-EG"] as const)[(random >>> 1) % 3]!,
          };
    const before = actual;
    if (change.kind === "LANGUAGE") {
      ({ language } = change);
    } else {
      ({ displayLocale } = change);
    }
    actual = changePreferences(actual, change);
    expect(actual).toEqual({ language, displayLocale });
    expect(before).not.toBe(actual);
  }
});
