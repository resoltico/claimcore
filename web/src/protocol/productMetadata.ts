import catalog from "@contracts/catalog.json" with { type: "json" };
import {
  consumerContractIdentity,
  type ProductMetadata,
  type CatalogDocument,
} from "@contracts/client";
export const productMetadata: ProductMetadata = {
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- The JSON and its type are emitted by the same generator.
  catalog: catalog as CatalogDocument,
  productVersion: catalog.productVersion,
  contractIdentity: consumerContractIdentity,
};
