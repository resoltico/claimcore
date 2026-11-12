import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";
const id = "00000000-0000-4000-8000-000000000001";
const handle = { noteId: id, canRemove: true };
const context = {
  kind: "NOTE_PAGE",
  caseId: id,
  nextCursor: "next",
  tasks: ["NOTE:REMOVE_NOTE"],
  notes: [handle],
};
const document = {
  screen: {
    regions: [
      {
        role: "NOTES",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "noteText",
              labelId: "field.noteText.label",
              prominence: "SECONDARY",
              isMoney: false,
            },
          ],
          rows: [{ key: id, values: [{ kind: "NARRATIVE", value: "Core note narrative Ω" }] }],
        },
      },
    ],
  },
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
};
const reply = (value: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: value },
});
it("admits keyed note controls independently of core narrative", () => {
  expect(isResponse("case.notes", reply(document))).toBe(true);
  expect(isResponse("case.timeline", reply(document))).toBe(false);
});
it("refuses missing, duplicate, unoffered and tightened note controls", () => {
  for (const notes of [
    [],
    [handle, handle],
    [{ ...handle, noteId: "00000000-0000-4000-8000-000000000002" }],
  ]) {
    expect(isResponse("case.notes", reply({ ...document, context: { ...context, notes } }))).toBe(
      false,
    );
  }
  for (const tasks of [[], ["CASE:ERASE"], ["NOTE:REMOVE_NOTE", "NOTE:REMOVE_NOTE"]]) {
    expect(isResponse("case.notes", reply({ ...document, context: { ...context, tasks } }))).toBe(
      false,
    );
  }
  expect(isResponse("case.notes", reply({ ...document, disclosure: "WITHHELD" }))).toBe(false);
});
