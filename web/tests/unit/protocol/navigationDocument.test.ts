import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const id = "00000000-0000-4000-8000-000000000001";
const parent = "00000000-0000-4000-8000-000000000002";
const screen = {
  regions: [
    {
      role: "RESULTS",
      content: {
        kind: "TABLE",
        columns: [
          { key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
        ],
        rows: [{ key: id, values: [{ kind: "TEXT", value: "opaque-list-caption Ω" }] }],
      },
    },
  ],
};
const document = (context: unknown) => ({
  screen,
  context,
  disclosure: "CURRENT",
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
});
const reply = (context: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: document(context) },
});

it("admits bounded case, reminder and person destinations independent of opaque captions", () => {
  expect(
    isResponse("case.list", reply({ kind: "CASES", nextCursor: "next", tasks: [], caseIds: [id] })),
  ).toBe(true);
  expect(
    isResponse(
      "reminder.list",
      reply({
        kind: "REMINDERS",
        nextCursor: "next",
        reminders: [{ caseId: parent, reminderId: id }],
      }),
    ),
  ).toBe(true);
  expect(
    isResponse("authority.list", reply({ kind: "PEOPLE", nextCursor: "next", actorIds: [id] })),
  ).toBe(true);
});

it("refuses missing or duplicate destinations and wrong list resource kinds", () => {
  for (const caseIds of [[], [id, id]]) {
    expect(
      isResponse("case.list", reply({ kind: "CASES", nextCursor: null, tasks: [], caseIds })),
    ).toBe(false);
  }
  expect(
    isResponse("reminder.list", reply({ kind: "REMINDERS", nextCursor: null, reminders: [] })),
  ).toBe(false);
  expect(
    isResponse("authority.list", reply({ kind: "PEOPLE", nextCursor: null, actorIds: [] })),
  ).toBe(false);
  expect(isResponse("case.list", reply({ kind: "PEOPLE", nextCursor: null, actorIds: [id] }))).toBe(
    false,
  );
});

it("refuses listed destinations after successful disclosure tightening", () => {
  const value = reply({ kind: "CASES", nextCursor: null, tasks: [], caseIds: [id] });
  expect(
    isResponse("case.list", {
      ...value,
      outcome: {
        ...value.outcome,
        document: { ...value.outcome.document, disclosure: "WITHHELD" },
      },
    }),
  ).toBe(false);
});
