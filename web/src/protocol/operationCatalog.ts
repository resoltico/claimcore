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

/** Catalog identity may be qualified while the typed wire action is family-local. */
export const operationEntryForInput = (input: {
  readonly kind: string;
  readonly act: { readonly kind: string };
}): CatalogEntry => {
  const definition = productMetadata.catalog.entries.find(
    (item) =>
      item.subject === input.kind &&
      item.constants.some(
        (constant) => constant.path.join(".") === "act.kind" && constant.value === input.act.kind,
      ),
  );
  if (definition === undefined) {
    throw new Error("Unknown typed operation catalog entry.");
  }
  return definition;
};
