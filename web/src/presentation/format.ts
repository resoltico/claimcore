import type { FieldDescriptor } from "../api/v2";
import type { DisplayLocale } from "./preferences";

const digitsFor = (locale: DisplayLocale) => {
  const formatter = new Intl.NumberFormat(locale, { useGrouping: false });
  return Array.from({ length: 10 }, (_, n) => formatter.format(n));
};
export const exactInteger = (value: string, locale: DisplayLocale): string => {
  if (!/^(0|[1-9][0-9]{0,18})$/u.test(value)) return value;
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(BigInt(value));
};
/** Format only validated canonical decimal text; never round it or pass it through Number. */
export const exactAmount = (value: string, locale: DisplayLocale): string => {
  if (!/^(0|[1-9][0-9]{0,17})(\.[0-9]{1,4})?$/u.test(value)) return value;
  const [whole = "", fraction] = value.split(".");
  const integer = exactInteger(whole, locale);
  if (fraction === undefined) return integer;
  const separator = new Intl.NumberFormat(locale)
    .formatToParts(1.1)
    .find((part) => part.type === "decimal")!.value;
  const digits = digitsFor(locale);
  return (
    integer + separator + Array.from(fraction, (digit) => digits[digit.charCodeAt(0) - 48]).join("")
  );
};
const calendarCarrier = (value: string): Date | null => {
  if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/u.test(value)) return null;
  const year = Number(value.slice(0, 4));
  const month = Number(value.slice(5, 7));
  const day = Number(value.slice(8, 10));
  if (year === 0) return null;
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(0, 0, 0, 0);
  return date.getUTCFullYear() === year &&
    date.getUTCMonth() === month - 1 &&
    date.getUTCDate() === day
    ? date
    : null;
};
export const calendarDate = (value: string, locale: DisplayLocale): string => {
  const date = calendarCarrier(value);
  if (date === null) return value;
  const digits = digitsFor(locale);
  const year = Array.from(value.slice(0, 4), (d) => digits[d.charCodeAt(0) - 48]).join("");
  return new Intl.DateTimeFormat(locale, {
    calendar: "gregory",
    timeZone: "UTC",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  })
    .formatToParts(date)
    .map((part) => (part.type === "year" ? year : part.value))
    .join("");
};
export const displayField = (
  value: string,
  field: FieldDescriptor,
  locale: DisplayLocale,
): string => {
  if (field.scalar.kind === "AMOUNT") return exactAmount(value, locale);
  if (field.scalar.kind === "CALENDAR_DATE") return calendarDate(value, locale);
  return value;
};
