import { isAmountText } from "../protocol/scalars";
import { productMetadata } from "../protocol/productMetadata";
import {
  isLocalizedAmount,
  type FormValues,
  type FormValue,
  type LocalizedAmount,
} from "../protocol/forms";
import type { CatalogEntry } from "../protocol/catalogAssembly";

/** Ambiguous or malformed entry remains authored text for server admission. */
export const normalizeAmountEntry = (raw: string, locale: string): string => {
  if (isAmountText(raw)) {
    return raw;
  }
  const format = new Intl.NumberFormat(locale, { useGrouping: true });
  const parts = format.formatToParts(1234567.5);
  const decimal = parts.find((part) => part.type === "decimal")?.value ?? ".";
  const group = parts.find((part) => part.type === "group")?.value;
  const digits = new Map(
    Array.from({ length: 10 }, (_, digit) => [
      new Intl.NumberFormat(locale, { useGrouping: false }).format(digit),
      String(digit),
    ]),
  );
  const translated = Array.from(raw, (value) => digits.get(value) ?? value).join("");
  const pieces = translated.split(decimal);
  if (pieces.length > 2) {
    return raw;
  }
  const whole = pieces[0]!;
  const groups = group === undefined ? [whole] : whole.split(group);
  if (
    groups.length > 1 &&
    (!/^[0-9]{1,3}$/u.test(groups[0]!) ||
      groups.slice(1).some((value) => !/^[0-9]{3}$/u.test(value)))
  ) {
    return raw;
  }
  const normalized = groups.join("") + (pieces.length === 2 ? `.${pieces[1]}` : "");
  return isAmountText(normalized) ? normalized : raw;
};

const amountPaths = (entry: CatalogEntry): ReadonlySet<string> => {
  const fields = new Set<string>(
    productMetadata.catalog.fields
      .filter((field) => field.scalar.kind === "AMOUNT")
      .map((field) => field.name),
  );
  return new Set(
    entry.inputs
      .filter((input) => input.field !== null && fields.has(input.field))
      .map((input) => input.path.join(".")),
  );
};

export const captureAmountEntry = (
  path: string,
  value: FormValue,
  entry: CatalogEntry,
  locale: LocalizedAmount["locale"],
): FormValue =>
  typeof value === "string" && amountPaths(entry).has(path)
    ? { kind: "LOCALIZED_AMOUNT", text: value, locale }
    : value;

/** Prefills are already canonical strings. Only authored entries need their captured locale. */
export const normalizeAmountValues = (values: FormValues, entry: CatalogEntry): FormValues => {
  const paths = amountPaths(entry);
  return Object.fromEntries(
    Object.entries(values).map(([path, value]) => [
      path,
      paths.has(path) && isLocalizedAmount(value)
        ? normalizeAmountEntry(value.text, value.locale)
        : value,
    ]),
  );
};
