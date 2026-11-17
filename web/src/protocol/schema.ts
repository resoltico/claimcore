import { isRepresentable, strictByteProfile } from "@contracts/client";
const inProfile = (value: unknown, depth: number): boolean => {
    if (depth > strictByteProfile.maximumDepth) {return false;}
    if (typeof value === "string") {return isRepresentable(value);}
    if (typeof value === "number") {return Number.isSafeInteger(value);}
    if (Array.isArray(value)) {return value.every((item) => inProfile(item, depth + 1));}
    if (typeof value === "object" && value !== null)
      {return Object.entries(value).every(
        ([key, item]) => isRepresentable(key) && inProfile(item, depth + 1),
      );}
    return value === null || typeof value === "boolean";
  };
export const admitBytes = (bytes: Uint8Array): unknown => {
  if (bytes.length > strictByteProfile.maximumResponseBytes) {
    throw new Error("JSON exceeds its byte allowance.");
  }
  const value: unknown = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  if (!inProfile(value, 0)) {throw new Error("JSON exceeds its structural profile.");}
  return value;
};
