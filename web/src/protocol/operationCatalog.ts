import { productMetadata } from "./productMetadata";
import type { CatalogEntry, CatalogEntryIdentity } from "./catalogAssembly";

export const operationEntry = (identity: CatalogEntryIdentity): CatalogEntry => {
  const definition = productMetadata.catalog.entries.find(
    (item) => item.subject === identity.subject && item.kind === identity.kind,
  );
  if (definition === undefined) {
    throw new Error("Unknown operation catalog entry.");
  }
  return definition;
};
