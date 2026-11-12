import { localeNumbers } from "./localeNumbers";
import { isAmountText } from "../protocol/scalars";
import { productMetadata } from "../protocol/productMetadata";
import {
  isLocalizedAmount,
  type FormValues,
  type FormValue,
  type LocalizedAmount,
} from "../protocol/forms";
import type { OperationTaskDefinition } from "@contracts/client";
import { taskInput } from "../protocol/taskCatalog";

/** Ambiguous or malformed entry remains authored text for server admission. */
export const normalizeAmountEntry = (raw: string, locale: string): string => {
  const { decimal, group, digitValues: digits } = localeNumbers(locale);
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

const amountPaths = (entry: OperationTaskDefinition): ReadonlySet<string> => {
  const fields = new Set<string>(
    productMetadata.catalog.fields
      .filter((field) => field.scalar.kind === "AMOUNT")
      .map((field) => field.name),
  );
  return new Set(
    entry.inputs.filter((input) => fields.has(taskInput(input.id).key)).map((input) => input.id),
  );
};

export const captureAmountEntry = (
  path: string,
  value: FormValue,
  entry: OperationTaskDefinition,
  locale: LocalizedAmount["locale"],
): FormValue =>
  typeof value === "string" && amountPaths(entry).has(path)
    ? { kind: "LOCALIZED_AMOUNT", text: value, locale }
    : value;

/** Prefills are already canonical strings. Only authored entries need their captured locale. */
export const normalizeAmountValues = (
  values: FormValues,
  entry: OperationTaskDefinition,
): FormValues => {
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
