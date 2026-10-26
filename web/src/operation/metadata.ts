import type { CaseAct, CaseActOffer } from "@contracts/client";
import { productMetadata } from "../protocol/productMetadata";
export type ActKind = CaseAct["kind"];
export const isCaseAct = (value: string): value is ActKind =>
  productMetadata.catalog.entries.some(
    (entry) =>
      entry.subject === "CASE" && entry.endpoint === "operation.execute" && entry.kind === value,
  );

export const offerAct = (offer: CaseActOffer): ActKind =>
  offer.kind === "CORRECT" ? "CORRECT" : offer.act;
