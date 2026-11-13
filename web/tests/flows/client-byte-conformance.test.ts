import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { workBasis, actorRefInput } from "../support/inputs";
import { timelineEntry } from "../support/timeline.fixtures";
import { decodeResponse, isRequest } from "../../src/protocol/client";
import type { EndpointId } from "@contracts/client";
import { admitBytes } from "../../src/protocol/schema";

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
      resolve(import.meta.dirname, "../../../tests/fixtures/client-byte-conformance.json"),
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
  expect(samples.some((sample) => sample.valid)).toBe(true);
  expect(samples.some((sample) => !sample.valid)).toBe(true);
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

it("generated UUID admission consumes exactly one canonical nonempty UUID", () => {
  const uuid = "10000000-0000-4000-8000-00000000000a";
  expect(isRequest("operation.get", { operationId: uuid })).toBe(true);
  for (const value of [
    `${uuid}\n`,
    `${uuid}\r\n`,
    `${uuid}\u2028`,
    `${uuid}x`,
    uuid.toUpperCase(),
    "00000000-0000-0000-0000-000000000000",
  ]) {
    expect(isRequest("operation.get", { operationId: value }), value).toBe(false);
  }
});

it("bounds timeline, directory and grant pages at their declared limits", () => {
  const directory = {
    basis: workBasis,
    actor: {
      actor: actorRefInput(),
      principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "page-target" },
      contact: null,
      state: "NO_ROLES",
      roles: [],
    },
    offers: [],
    profileSeed: null,
    items: [],
    nextCursor: null,
  };
  for (const [count, valid] of [
    [0, true],
    [10, true],
    [11, true],
    [50, true],
    [51, false],
  ] as const) {
    const items = Array.from({ length: count }, () => directory.actor);
    const pages = [
      ["people.list", { basis: directory.basis, items, nextCursor: null }],
      [
        "people.grants",
        {
          ...directory,
          items: Array.from({ length: count }, () => ({
            role: "CASE_READER",
            scope: { kind: "INSTALLATION" },
            grantedAt: "2026-11-04T12:00:00.000000Z",
            grantedBy: directory.actor.actor,
          })),
        },
      ],
      [
        "case.timeline",
        {
          disclosure: "FULL",
          basis: directory.basis,
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "CASE-1",
          items: Array.from({ length: count }, () => timelineEntry()),
          nextCursor: null,
        },
      ],
    ] as const;
    for (const [endpoint, data] of pages) {
      const expected = endpoint === "case.timeline" ? count <= 10 : valid;
      expect(
        admitted({
          id: `${endpoint}-${count}`,
          endpoint,
          direction: "response",
          valid,
          text: JSON.stringify({
            representation: "DATA",
            outcome: { kind: "SUCCEEDED", data },
          }),
        }),
      ).toBe(expected);
    }
  }
});
