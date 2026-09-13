const isSuspiciousCodePoint = (value: number): boolean =>
  value <= 0x1f ||
  value === 0x7f ||
  (value >= 0x200b && value <= 0x200f) ||
  (value >= 0x202a && value <= 0x202e) ||
  (value >= 0x2060 && value <= 0x206f);

export const suspiciousCodePoints = (value: string): string[] => {
  return Array.from(value)
    .filter((point) => isSuspiciousCodePoint(point.codePointAt(0)!))
    .map((point) => `U+${point.codePointAt(0)!.toString(16).toUpperCase()}`);
};

export const hasSuspiciousCharacters = (value: string): boolean =>
  suspiciousCodePoints(value).length > 0;
