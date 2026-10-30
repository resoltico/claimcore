import { isAmountText, isGregorianDate } from "../protocol/scalars";
import type { FieldCatalogEntry } from "../api/service";
import type { DisplayLocale } from "./preferences";

const digitsFor = (locale: DisplayLocale) => {
  const formatter = new Intl.NumberFormat(locale, { useGrouping: false });
  return Array.from({ length: 10 }, (_, n) => formatter.format(n));
};
export const exactInteger = (value: string, locale: DisplayLocale): string => {
  if (!/^(0|[1-9][0-9]{0,18})$/u.test(value)) {
    return value;
  }
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(BigInt(value));
};
const currencies = new Set(Intl.supportedValuesOf("currency"));
const canonicalTotal = /^(0|[1-9][0-9]{0,39})(\.[0-9]{1,4})?$/u;
const fractionDigits = (currency: string | undefined, own: number): number =>
  currency !== undefined && currencies.has(currency)
    ? Math.max(
        own,
        new Intl.NumberFormat("en", { style: "currency", currency }).resolvedOptions()
          .maximumFractionDigits ?? 0,
      )
    : own;

/** Integer and fractional text stay exact; Intl supplies grouping, digits and currency padding only. */
const decimalText = (value: string, locale: DisplayLocale, currency?: string): string => {
  const [whole = "", own = ""] = value.split(".");
  const fraction = own.padEnd(fractionDigits(currency, own.length), "0");
  const integer = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(BigInt(whole));
  if (fraction === "") {
    return integer;
  }
  const separator = new Intl.NumberFormat(locale)
    .formatToParts(1.1)
    .find((part) => part.type === "decimal")!.value;
  const digits = digitsFor(locale);
  return (
    integer + separator + Array.from(fraction, (digit) => digits[digit.charCodeAt(0) - 48]).join("")
  );
};

export const exactAmount = (value: string, locale: DisplayLocale, currency?: string): string =>
  isAmountText(value) ? decimalText(value, locale, currency) : value;

export const exactMoney = (value: string, currency: string, locale: DisplayLocale): string =>
  `${canonicalTotal.test(value) ? decimalText(value, locale, currency) : value} ${currency}`;

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

const calendarCarrier = (value: string): Date | null =>
  isGregorianDate(value) ? new Date(`${value}T00:00:00.000Z`) : null;
export const calendarDate = (value: string, locale: DisplayLocale): string => {
  const date = calendarCarrier(value);
  if (date === null) {
    return value;
  }
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
  field: FieldCatalogEntry,
  locale: DisplayLocale,
): string => {
  if (field.scalar.kind === "AMOUNT") {
    return exactAmount(value, locale);
  }
  if (field.scalar.kind === "CALENDAR_DATE") {
    return calendarDate(value, locale);
  }
  return value;
};
