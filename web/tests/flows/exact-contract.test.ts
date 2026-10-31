import { expect, it } from "vitest";
import { admitBytes } from "../../src/protocol/schema";
import { isRequest, isResponse, decodeResponse } from "../../src/protocol/client";
import { corpusCases, wireReply } from "../support/contract-corpus.fixtures";
it("admits every positive and rejects every negative authored endpoint examples case from bytes", () => {
  const cases = corpusCases();
  expect(cases.some((sample) => !sample.valid)).toBe(true);
  for (const sample of cases) {
    const bytes = new TextEncoder().encode(JSON.stringify(sample.value));
    let valid: boolean;
    try {
      if (sample.direction === "request") {
        valid = isRequest(sample.endpoint, admitBytes(bytes));
      } else {
        decodeResponse(sample.endpoint, bytes);
        valid = true;
      }
    } catch {
      valid = false;
    }
    expect(valid, sample.id).toBe(sample.valid);
  }
});
it("independently rejects lossy numeric spellings, duplicate decoded names, depth, Unicode and UTF-8 boundaries", () => {
  const invalid = [
    '{"limit":1.0}',
    '{"limit":1e0}',
    '{"limit":9007199254740992}',
    '{"limit":1,"l\\u0069mit":2}',
    '{"limit":1,"extra":true}',
    '{"limit":0}',
    '{"limit":201}',
    '{"limit":1,"cursor":"\\ud800"}',
    `${"[".repeat(33)}0${"]".repeat(33)}`,
  ];
  for (const text of invalid) {
    let valid: boolean;
    try {
      valid = isRequest("case.list", admitBytes(new TextEncoder().encode(text)));
    } catch {
      valid = false;
    }
    expect(valid, text).toBe(false);
  }
  expect(() => admitBytes(new Uint8Array([0xff]))).toThrow();
  expect(
    isRequest(
      "operation.execute",
      admitBytes(
        new TextEncoder().encode(
          '{"kind":"CASE","operationId":"10000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"A"},"expectedRevision":-0,"act":{"kind":"CLOSE"}}',
        ),
      ),
    ),
  ).toBe(true);
  expect(() =>
    decodeResponse(
      "session",
      new TextEncoder().encode(
        `{"endpoint":"session","outcome":{"kind":"SUCCEEDED","data":{"authenticated":true,"contractIdentity":"${"a".repeat(64)}","antiforgeryToken":null,"unexpected":true}}}`,
      ),
    ),
  ).toThrow();
});
it("admits business date strings for typed Domain rejection", () => {
  const base = {
    kind: "CASE",
    operationId: "10000000-0000-4000-8000-000000000001",
    target: { kind: "REFERENCE", caseReference: "A" },
    expectedRevision: 0,
    act: {
      kind: "RECORD_PAYMENT",
      values: {
        paymentDate: "2024-02-29",
      },
    },
  };
  expect(isRequest("operation.execute", base)).toBe(true);
  expect(
    isRequest("operation.execute", {
      ...base,
      act: {
        ...base.act,
        values: {
          paymentDate: "2023-02-29",
        },
      },
    }),
  ).toBe(true);
  expect(
    isRequest("operation.execute", {
      ...base,
      act: {
        ...base.act,
        values: {
          paymentDate: "0000-02-29",
        },
      },
    }),
  ).toBe(true);
});
it("requires the owned canonical timestamp spelling rather than permissive host parsing", () => {
  const source = wireReply("operation.execute");
  if (source.outcome.kind !== "ACCEPTED") {
    throw new Error("Missing independent receipt fixture.");
  }
  const reply = (recordedAt: string) => ({
    ...source,
    outcome: {
      ...source.outcome,
      data: {
        ...source.outcome.data,
        recordedAt,
      },
    },
  });
  expect(isResponse("operation.execute", reply("2026-10-16T12:34:56.123456Z"))).toBe(true);
  for (const timestamp of [
    "2026-10-16T12:34:56.1234560+14:59",
    "2026-10-16T12:34:56.1234560+00:00",
    "2026-10-16T12:34:56.1234560+00:00\n",
    "2026-02-30T12:34:56.1234560+00:00",
  ]) {
    expect(isResponse("operation.execute", reply(timestamp))).toBe(false);
  }
});
