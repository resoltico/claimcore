import type { Terms } from "@contracts/messages";
import { isTerm } from "./terms";
import { productMetadata } from "../protocol/productMetadata";

export const queryChoice = <V extends keyof Terms>(endpoint: string, vocabulary: V) => {
  const entry = productMetadata.catalog.entries.find(
    (candidate) => candidate.endpoint === endpoint,
  );
  const input = entry?.inputs.find(
    (candidate) => candidate.kind.kind === "CHOICE" && candidate.kind.vocabulary === vocabulary,
  );
  if (input?.kind.kind !== "CHOICE") {
    throw new Error("Missing declared query choice.");
  }
  const options = input.kind.options.map((value): Terms[V] => {
    if (!isTerm(vocabulary, value)) {
      throw new Error("Unknown declared query choice.");
    }
    return value;
  });
  return { labelId: input.labelId, options };
};
