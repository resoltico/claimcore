import { productMetadata } from "../protocol/productMetadata";

const policy = productMetadata.descriptors.characterWarnings;
const suspiciousCategory = new RegExp(
  `[${policy.categories.map((category) => `\\p{${category}}`).join("")}]`,
  "u",
);

export const suspiciousCodePoints = (value: string): string[] => {
  const points = new Set<string>();
  for (const point of value) {
    if (!suspiciousCategory.test(point)) {
      continue;
    }
    points.add(`U+${point.codePointAt(0)!.toString(16).toUpperCase()}`);
    if (points.size > policy.maximumCodePoints) {
      return [...points].slice(0, policy.maximumCodePoints).concat("…");
    }
  }
  return [...points];
};

export const hasSuspiciousCharacters = (value: string): boolean =>
  suspiciousCodePoints(value).length > 0;
