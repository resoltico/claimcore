import descriptors from "@contracts/descriptors.json" with { type: "json" };
import { wireContractFingerprint, type ProductMetadata } from "@contracts/client";
export const productMetadata: ProductMetadata = {
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- JSON and its descriptor type are emitted together and checked by contract freshness.
  descriptors: descriptors as ProductMetadata["descriptors"],
  productVersion: descriptors.productVersion,
  wireFingerprint: wireContractFingerprint,
};
