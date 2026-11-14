import { strictByteProfile } from "@contracts/client";
export const admitBytes = (bytes: Uint8Array): unknown => {
  if (bytes.length > strictByteProfile.maximumResponseBytes) {
    throw new Error("JSON exceeds its byte allowance.");
  }
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) as unknown;
};
