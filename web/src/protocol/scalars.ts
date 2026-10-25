import { productMetadata } from "./productMetadata";
import { isGregorianDate } from "./schema";
const amount = productMetadata.catalog.fields
  .map((field) => field.scalar)
  .find((scalar) => scalar.kind === "AMOUNT");
export const isAmountText = (value: string): boolean =>
  amount !== undefined && new RegExp(`^(?:${amount.grammar})(?![\\s\\S])`, "u").test(value);
export { isGregorianDate };
