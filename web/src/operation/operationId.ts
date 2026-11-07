/** RFC 9562 UUIDv7: Unix milliseconds followed by random bits, with version and variant fixed. */
export const operationId = (): string => {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  let milliseconds = Date.now();
  for (let index = 5; index >= 0; index -= 1) {
    bytes[index] = milliseconds % 256;
    milliseconds = Math.floor(milliseconds / 256);
  }
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
};

export const operationIssuedAt = (id: string): number =>
  Number.parseInt(id.replaceAll("-", "").slice(0, 12), 16);
