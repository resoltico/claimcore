import descriptors from "@contracts/descriptors.json" with { type: "json" };
import {
  consumerContractIdentity,
  isDescriptors,
  type ProductMetadata,
  type Descriptors,
} from "@contracts/client";
const admitDescriptors = (value: unknown): Descriptors => {
  if (!isDescriptors(value)) {
    throw new Error("Invalid generated presentation descriptors.");
  }
  return value;
};
export const productMetadata: ProductMetadata = {
  descriptors: admitDescriptors(descriptors),
  productVersion: descriptors.productVersion,
  contractIdentity: consumerContractIdentity,
};
