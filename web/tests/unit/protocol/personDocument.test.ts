import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const context = {
  kind: "PERSON",
  actorId: "00000000-0000-4000-8000-000000000001",
  principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "person-control" },
  nextCursor: "next",
  tasks: ["people.setProfile"],
  profileSeed: { displayName: "Editable input", contact: null },
  revocations: [],
  responsibleCases: null,
};
const document = {
  screen: { regions: [] },
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

it("admits person identity and permitted editable profile independently of saved display", () => {
  expect(isResponse("people.grants", reply(document))).toBe(true);
  expect(isResponse("people.history", reply(document))).toBe(false);
  expect(
    isResponse(
      "people.grants",
      reply({ ...document, context: { ...context, tasks: [], profileSeed: null } }),
    ),
  ).toBe(true);
});

it("refuses a profile seed without its task, creation tasks and tightened disclosure", () => {
  for (const tasks of [[], ["people.register"], ["people.setProfile", "people.setProfile"]]) {
    expect(
      isResponse("people.grants", reply({ ...document, context: { ...context, tasks } })),
    ).toBe(false);
  }
  expect(isResponse("people.grants", reply({ ...document, disclosure: "WITHHELD" }))).toBe(false);
});
