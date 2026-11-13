import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const ids = ["00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000002"];
const document = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "actor",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: ids.map((key) => ({
            key,
            values: [{ kind: "TEXT", value: `opaque-caption:${key}` }],
          })),
        },
      },
    ],
  },
  context: { kind: "ASSIGNEES", actorIds: ids, nextCursor: "choice-next" },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (value: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: value },
});

it("admits core-authored opaque assignee captions with exact ordered identity handles", () => {
  expect(isResponse("case.assignees", reply(document))).toBe(true);
  expect(JSON.stringify(document)).not.toContain('"displayName":');
});

it("rejects missing, extra, duplicate, reordered and undisclosed assignee handles", () => {
  for (const actorIds of [
    ids.slice(1),
    [...ids, "00000000-0000-4000-8000-000000000003"],
    [ids[0], ids[0]],
    [...ids].reverse(),
  ]) {
    expect(
      isResponse(
        "case.assignees",
        reply({ ...document, context: { ...document.context, actorIds } }),
      ),
    ).toBe(false);
  }
  expect(isResponse("case.assignees", reply({ ...document, disclosure: "WITHHELD" }))).toBe(false);
});

it("rejects an otherwise valid choice document at a different read endpoint", () => {
  expect(isResponse("case.get", reply(document))).toBe(false);
  expect(isResponse("people.list", reply(document))).toBe(false);
  expect(isResponse("case.assignees", reply(document))).toBe(true);
});
