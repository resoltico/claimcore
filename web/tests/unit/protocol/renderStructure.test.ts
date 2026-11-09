import { expect, it } from "vitest";
import { isRequest, isResponse } from "@contracts/client";

const reply = (view: unknown) => ({
  outcome: {
    kind: "SUCCEEDED",
    data: {
      basis: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
      items: [],
      nextCursor: null,
    },
    view,
  },
});

it("admits an opaque screen while rejecting duplicate non-fact roles", () => {
  const content = { kind: "ENTRIES", entries: [] };
  expect(isResponse("case.assignees", reply({ regions: [{ role: "WORK", content }] }))).toBe(true);
  expect(
    isResponse(
      "case.assignees",
      reply({
        regions: [
          { role: "WORK", content },
          { role: "WORK", content },
        ],
      }),
    ),
  ).toBe(false);
  expect(
    isResponse(
      "case.assignees",
      reply({
        regions: [
          { role: "FACTS", content },
          { role: "FACTS", content },
        ],
      }),
    ),
  ).toBe(true);
});

it("requires unique table column and row identities and a value for each column", () => {
  const column = {
    key: "one",
    labelId: "label.details",
    prominence: "PRIMARY",
    isMoney: false,
  };
  const row = { key: "row-a", values: [{ kind: "TEXT", value: "Authored" }] };
  const table = (columns: unknown[], rows: unknown[]) =>
    reply({ regions: [{ role: "RESULTS", content: { kind: "TABLE", columns, rows } }] });
  expect(isResponse("case.assignees", table([column], [row]))).toBe(true);
  expect(isResponse("case.assignees", table([column, column], []))).toBe(false);
  expect(isResponse("case.assignees", table([], [row]))).toBe(false);
  expect(isResponse("case.assignees", table([column], [row, row]))).toBe(false);
});

it("rejects duplicate presented message arguments without interpreting the financial meter", () => {
  const argument = { key: "canary", value: { kind: "TEXT", value: "Core-owned" } };
  const meter = (arguments_: unknown[]) =>
    reply({
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [],
            rows: [],
            meter: {
              current: { amount: "1", currency: "EUR", displayScale: 2 },
              target: { amount: "100", currency: "EUR", displayScale: 2 },
              complete: false,
              description: { messageId: "label.details", arguments: arguments_ },
            },
          },
        },
      ],
    });
  expect(isResponse("case.assignees", meter([argument]))).toBe(true);
  expect(isResponse("case.assignees", meter([argument, argument]))).toBe(false);
});

it("does not turn the issuer URI shape into a second canonical-principal admission rule", () => {
  expect(
    isRequest("operation.execute", {
      kind: "AUTHORITY",
      operationId: "019aa6a0-0000-7000-8000-000000000001",
      target: { kind: "HUMAN", issuer: "https://issuer.example:bad/", subject: "person" },
      act: { kind: "ENABLE" },
    }),
  ).toBe(true);
});

it("applies the same structural issuer projection profile without admitting a trusted principal", () => {
  const projection = (issuer: string) => ({
    outcome: {
      kind: "SUCCEEDED",
      view: { regions: [] },
      data: {
        basis: {
          observedAt: "2026-11-09T12:00:00.000000Z",
          businessDate: "2026-11-09",
          timeZone: "Etc/UTC",
        },
        items: [
          {
            principal: { kind: "HUMAN", issuer, subject: "person" },
            firstSeen: "2026-11-09T12:00:00.000000Z",
            lastSeen: "2026-11-09T12:00:00.000000Z",
            claims: {},
          },
        ],
        nextCursor: null,
      },
    },
  });
  expect(isResponse("authority.requests", projection("https://issuer.example:bad/"))).toBe(true);
  expect(isResponse("authority.requests", projection("https://issuer.example/"))).toBe(true);
  for (const issuer of ["https://issuer.example/\u0085", "https://issuer.example/\uFEFF"]) {
    expect(isResponse("authority.requests", projection(issuer))).toBe(false);
  }
});
