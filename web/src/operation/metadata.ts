import type { CaseAct } from "@contracts/client";
import { productMetadata } from "../protocol/productMetadata";
export type ActKind = CaseAct["kind"];
export const isCaseAct = (value: string): value is ActKind =>
  productMetadata.catalog.entries.some(
    (entry) =>
      entry.scope === "CASE" && entry.endpoint === "operation.execute" && entry.kind === value,
  );
