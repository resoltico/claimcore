import { productMetadata } from "./productMetadata";
const amount = productMetadata.catalog.fields
  .map((field) => field.scalar)
  .find((scalar) => scalar.kind === "AMOUNT");
export const isAmountText = (value: string): boolean =>
  amount !== undefined && new RegExp(`^(?:${amount.grammar})(?![\\s\\S])`, "u").test(value);
export const isGregorianDate = (value: string): boolean => {
  const match = /^([0-9]{4})-([0-9]{2})-([0-9]{2})$/u.exec(value);
  if (match === null) {
    return false;
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= (days[month - 1] ?? 0);
};
