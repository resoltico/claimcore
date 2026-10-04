const suspiciousCategory = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u;

export const suspiciousCodePoints = (value: string): string[] =>
  Array.from(value)
    .filter((point) => suspiciousCategory.test(point))
    .map((point) => `U+${point.codePointAt(0)!.toString(16).toUpperCase()}`);

export const hasSuspiciousCharacters = (value: string): boolean =>
  suspiciousCodePoints(value).length > 0;
