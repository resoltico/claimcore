import type { MessageValues } from "intl-messageformat";
import { expect, it, vi } from "vitest";
import { translate } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";

const observed = vi.hoisted(() => ({ constructions: 0 }));

// Observe construction while retaining the actual ICU parser and formatter implementation.
vi.mock(import("intl-messageformat"), async (importOriginal) => {
  const actual = await importOriginal();
  return {
    ...actual,
    default: class<V extends MessageValues | undefined = undefined> extends actual.default<V> {
      constructor(...parameters: ConstructorParameters<typeof actual.default>) {
        super(...parameters);
        observed.constructions += 1;
      }
    },
  };
});

it("reuses actual ICU formatters only within one language, display locale and message key", () => {
  const before = observed.constructions;
  expect(translate(defaults, "ui.attemptCount", { count: 1 })).toBe("1 attempt shown.");
  expect(translate(defaults, "ui.attemptCount", { count: 3 })).toBe("3 attempts shown.");
  expect(observed.constructions).toBe(before + 1);
  const englishArabicDigits = { ...defaults, displayLocale: "ar-EG" as const };
  expect(translate(englishArabicDigits, "ui.attemptCount", { count: 3 })).toBe("٣ attempts shown.");
  const arabicEnglishDigits = { ...defaults, language: "ar" as const };
  expect(translate(arabicEnglishDigits, "ui.attemptCount", { count: 3 })).toBe(
    "أدلة المحاولات: 3 محاولات معروضة.",
  );
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(observed.constructions).toBe(before + 4);
  expect(translate(englishArabicDigits, "ui.attemptCount", { count: 11 })).toBe(
    "١١ attempts shown.",
  );
  expect(translate(arabicEnglishDigits, "ui.attemptCount", { count: 2 })).toBe(
    "أدلة المحاولات: محاولتان معروضتان.",
  );
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(observed.constructions).toBe(before + 4);
});
