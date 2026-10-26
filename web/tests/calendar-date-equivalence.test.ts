import { expect, it } from "vitest";
import { isGregorianDate } from "@contracts/client";

const platformDate = (value: string): boolean => {
  if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/u.test(value) || value.startsWith("0000")) {
    return false;
  }
  const date = new Date(`${value}T00:00:00.000Z`);
  return Number.isFinite(date.valueOf()) && date.toISOString().slice(0, 10) === value;
};
it("arithmetic calendar admission equals the prior platform behavior across 4,620,007 inputs", () => {
  const mismatches: string[] = [];
  let checked = 0;
  const check = (value: string) => {
    checked++;
    if (isGregorianDate(value) !== platformDate(value)) {
      mismatches.push(value);
    }
  };
  for (let year = 0; year <= 9999; year++) {
    for (let month = 0; month <= 13; month++) {
      for (let day = 0; day <= 32; day++) {
        check(
          `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`,
        );
      }
    }
  }
  for (const value of [
    "2026-1-01",
    "2026-01-01T00",
    " 2026-01-01",
    "２０２６-01-01",
    "+02026-01-01",
    "",
    "abcd-ef-gh",
  ]) {
    check(value);
  }
  expect(checked).toBe(4_620_007);
  expect(mismatches).toEqual([]);
}, 30_000);
