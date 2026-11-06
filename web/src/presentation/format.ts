import { productMetadata } from "../protocol/productMetadata";
import { isAmountText, isGregorianDate } from "../protocol/scalars";
import type { FieldCatalogEntry } from "../api/service";
import { displayLocales, type DisplayLocale } from "./preferences";
import { localeNumbers } from "./localeNumbers";

export const exactInteger = (value: string, locale: DisplayLocale): string => {
  if (!/^(0|[1-9][0-9]{0,18})$/u.test(value)) {
    return value;
  }
  return localeNumbers(locale).integer.format(BigInt(value));
};
const currencyMinorUnits = new Map(
  productMetadata.catalog.moneyMinorUnits.map(({ currency, minorUnits }) => [currency, minorUnits]),
);
const canonicalTotal = /^(0|[1-9][0-9]{0,39})(\.[0-9]{1,4})?$/u;
const fractionDigits = (currency: string | undefined, own: number): number =>
  Math.max(own, currency === undefined ? 0 : (currencyMinorUnits.get(currency) ?? 0));

/** Integer and fractional text stay exact; Intl supplies grouping and digits; currency padding comes from the core catalog. */
const decimalText = (
  value: string,
  locale: DisplayLocale,
  minimumFractionDigits: number,
): string => {
  const [whole = "", own = ""] = value.split(".");
  const fraction = own.padEnd(minimumFractionDigits, "0");
  const numbers = localeNumbers(locale);
  const integer = numbers.integer.format(BigInt(whole));
  if (fraction === "") {
    return integer;
  }
  const { decimal: separator, digits } = numbers;
  return (
    integer + separator + Array.from(fraction, (digit) => digits[digit.charCodeAt(0) - 48]).join("")
  );
};

export const exactAmount = (value: string, locale: DisplayLocale, currency?: string): string =>
  isAmountText(value)
    ? decimalText(value, locale, fractionDigits(currency, value.split(".")[1]?.length ?? 0))
    : value;

export const exactMoney = (value: string, currency: string, locale: DisplayLocale): string =>
  `${canonicalTotal.test(value) ? decimalText(value, locale, fractionDigits(currency, value.split(".")[1]?.length ?? 0)) : value} ${currency}`;

/** The review's supplied display scale controls padding; exact fractional text is never truncated. */
export const moneyWithScale = (
  value: string,
  currency: string,
  displayScale: number,
  locale: DisplayLocale,
): string => `${decimalText(value, locale, displayScale)} ${currency}`;

export const caseAge = (
  notified: string | null,
  businessDate: string,
  locale: DisplayLocale,
): string => {
  if (notified === null || !isGregorianDate(notified) || !isGregorianDate(businessDate)) {
    return "";
  }
  const days =
    (new Date(`${businessDate}T00:00:00Z`).getTime() -
      new Date(`${notified}T00:00:00Z`).getTime()) /
    86400000;
  return new Intl.NumberFormat(locale, {
    style: "unit",
    unit: "day",
    unitDisplay: "long",
    maximumFractionDigits: 0,
  }).format(days);
};

export const recordedTime = (value: string, zone: string, locale: DisplayLocale): string =>
  new Intl.DateTimeFormat(locale, {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: zone,
  }).format(new Date(value));

const numericDates = new Map<DisplayLocale, Intl.DateTimeFormat>();
const namedDates = new Map<DisplayLocale, Intl.DateTimeFormat>();
const calendarFormatter = (locale: DisplayLocale, named: boolean): Intl.DateTimeFormat => {
  const cache = named ? namedDates : numericDates;
  const existing = cache.get(locale);
  if (existing !== undefined) {
    return existing;
  }
  const formatter = new Intl.DateTimeFormat(locale, {
    calendar: "gregory",
    timeZone: "UTC",
    year: "numeric",
    month: named ? "short" : "2-digit",
    day: named ? "numeric" : "2-digit",
    ...(named ? { weekday: "short" as const } : {}),
  });
  if (displayLocales.includes(locale)) {
    cache.set(locale, formatter);
  }
  return formatter;
};

const calendarCarrier = (value: string): Date | null =>
  isGregorianDate(value) ? new Date(`${value}T00:00:00.000Z`) : null;
export const calendarDateParts = (
  value: string,
  locale: DisplayLocale,
  named = false,
): ReadonlyArray<Intl.DateTimeFormatPart> => {
  const date = calendarCarrier(value);
  if (date === null) {
    return [{ type: "literal", value }];
  }
  const { digits } = localeNumbers(locale);
  const year = Array.from(value.slice(0, 4), (d) => digits[d.charCodeAt(0) - 48]).join("");
  return calendarFormatter(locale, named)
    .formatToParts(date)
    .map((part) => ({ type: part.type, value: part.type === "year" ? year : part.value }));
};
export const calendarDate = (value: string, locale: DisplayLocale, named = false): string =>
  calendarDateParts(value, locale, named)
    .map((part) => part.value)
    .join("");
export const displayField = (
  value: string,
  field: FieldCatalogEntry,
  locale: DisplayLocale,
): string => {
  if (field.scalar.kind === "AMOUNT") {
    return exactAmount(value, locale);
  }
  if (field.scalar.kind === "CALENDAR_DATE" || field.scalar.kind === "PLANNED_DATE") {
    return calendarDate(value, locale);
  }
  return value;
};
