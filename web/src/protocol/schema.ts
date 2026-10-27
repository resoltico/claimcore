import { strictByteProfile } from "@contracts/client";
import { strictJsonBytes } from "./strictJson";

// The strict profile preserves integer lexical spelling before JSON.parse loses it.
export const admitBytes = (bytes: Uint8Array): unknown => {
  if (bytes.length > strictByteProfile.maximumResponseBytes) {
    throw new Error("JSON exceeds its byte allowance.");
  }
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  const tokens =
    text.match(/"(?:[^"\\]|\\.)*"|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/gu) ?? [];
  if (tokens.some((token) => !token.startsWith('"') && /[.eE]/u.test(token))) {
    throw new Error("Integer wire values require integer spelling.");
  }
  return strictJsonBytes(bytes);
};

export { isGregorianDate } from "@contracts/client";
