import { productMetadata } from "../protocol/productMetadata";
export type ActKind = (typeof productMetadata.catalog.acts)[number]["kind"];
export const isCaseAct = (value: string): value is ActKind =>
  productMetadata.catalog.acts.some((act) => act.kind === value);
