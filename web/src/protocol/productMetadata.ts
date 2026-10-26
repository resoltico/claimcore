import catalog from "@contracts/catalog.json" with { type: "json" };
import {
  consumerContractIdentity,
  isCatalogDocument,
  type ProductMetadata,
  type CatalogDocument,
} from "@contracts/client";
const admitCatalogDocument = (value: unknown): CatalogDocument => {
  if (!isCatalogDocument(value)) {
    throw new Error("Invalid generated presentation catalog.");
  }
  return value;
};
export const productMetadata: ProductMetadata = {
  catalog: admitCatalogDocument(catalog),
  productVersion: catalog.productVersion,
  contractIdentity: consumerContractIdentity,
};
