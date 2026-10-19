import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { decodeResponse, isRequest } from "../src/protocol/client";
import type { EndpointId } from "@contracts/client";
import { admitBytes, validateSchema } from "../src/protocol/schema";

type ByteCase = {
  id: string;
  endpoint: EndpointId;
  direction: "request" | "response";
  valid: boolean;
  text?: string;
  hex?: string;
  serverIdentityValid?: boolean;
};
const samples = (
  JSON.parse(
    readFileSync(
      resolve(import.meta.dirname, "../../tests/fixtures/client-byte-conformance.json"),
      "utf8",
    ),
  ) as { cases: ByteCase[] }
).cases;
const payload = (sample: ByteCase): Uint8Array =>
  sample.hex === undefined
    ? new TextEncoder().encode(sample.text)
    : Uint8Array.from(Buffer.from(sample.hex, "hex"));
const admitted = (sample: ByteCase): boolean => {
  try {
    if (sample.direction === "response") {
      decodeResponse(sample.endpoint, payload(sample));
      return true;
    }
    return isRequest(sample.endpoint, admitBytes(payload(sample)));
  } catch {
    return false;
  }
};

it("runs independent positive and negative raw byte cases through production browser decoders", () => {
  expect(samples).toHaveLength(50);
  for (const sample of samples) {
    expect(admitted(sample), sample.id).toBe(sample.valid);
  }
});

it("keeps principal wire admission separate from authoritative server identity policy", () => {
  const principals = samples.filter((sample) => sample.serverIdentityValid !== undefined);
  expect(principals).toHaveLength(22);
  expect(principals.some((sample) => sample.serverIdentityValid === false)).toBe(true);
  for (const principal of principals) {
    expect(admitted(principal), principal.id).toBe(principal.valid);
  }
});

it("UUID format without a length consumes exactly one canonical nonempty UUID", () => {
  const schema = { type: "string", format: "uuid" };
  const uuid = "10000000-0000-4000-8000-00000000000a";
  expect(validateSchema(schema, uuid)).toBe(true);
  for (const value of [
    `${uuid}\n`,
    `${uuid}\r\n`,
    `${uuid}\u2028`,
    `${uuid}x`,
    uuid.toUpperCase(),
    "00000000-0000-0000-0000-000000000000",
  ]) {
    expect(validateSchema(schema, value), value).toBe(false);
  }
});
