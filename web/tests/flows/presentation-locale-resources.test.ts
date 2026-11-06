import { expect, it, vi } from "vitest";

it("reuses supported fixed number/date profiles without losing exact values", async () => {
  vi.resetModules();
  const { exactMoney, calendarDate } = await import("../../src/presentation/format");
  const { normalizeAmountEntry } = await import("../../src/presentation/amountEntry");
  const numbers = vi.spyOn(Intl, "NumberFormat");
  const dates = vi.spyOn(Intl, "DateTimeFormat");
  try {
    expect(exactMoney("12345.6700", "EUR", "de-CH")).toBe("12'345.6700 EUR");
    expect(normalizeAmountEntry("12'345.6700", "de-CH")).toBe("12345.6700");
    expect(calendarDate("0001-01-01", "en-GB")).toBe("01/01/0001");
    expect(calendarDate("2026-11-06", "en-GB", true)).toBe("Fri, 6 Nov 2026");
    expect(numbers.mock.calls.length).toBeGreaterThan(0);
    expect(dates).toHaveBeenCalledTimes(2);
    numbers.mockClear();
    dates.mockClear();
    for (let index = 0; index < 10; index++) {
      expect(exactMoney("999999999999999999.0100", "EUR", "de-CH")).toBe(
        "999'999'999'999'999'999.0100 EUR",
      );
      expect(normalizeAmountEntry("12'34.50", "de-CH")).toBe("12'34.50");
      expect(calendarDate("0001-01-01", "en-GB")).toBe("01/01/0001");
      expect(calendarDate("2026-11-06", "en-GB", true)).toBe("Fri, 6 Nov 2026");
    }
    expect(numbers).not.toHaveBeenCalled();
    expect(dates).not.toHaveBeenCalled();
  } finally {
    vi.restoreAllMocks();
  }
});

it("does not retain arbitrary Intl locale inputs or change malformed-locale treatment", async () => {
  vi.resetModules();
  const { normalizeAmountEntry } = await import("../../src/presentation/amountEntry");
  const numbers = vi.spyOn(Intl, "NumberFormat");
  try {
    expect(normalizeAmountEntry("1.234,50", "it-IT")).toBe("1234.50");
    numbers.mockClear();
    expect(normalizeAmountEntry("1.234,50", "it-IT")).toBe("1234.50");
    expect(numbers.mock.calls.length).toBeGreaterThan(0);
    expect(() => normalizeAmountEntry("1", "not_a_locale")).toThrow(RangeError);
  } finally {
    vi.restoreAllMocks();
  }
});
