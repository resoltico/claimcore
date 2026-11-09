import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const document = {
  screen: {
    regions: [],
    currentGroups: ["registration", "decision"].map((group) => ({
      group,
      section: {
        titleId: "label.details",
        entries: [
          {
            key: "opaque-before",
            labelId: "label.details",
            prominence: "PRIMARY",
            value: { kind: "TEXT", value: `core-before:${group}` },
          },
        ],
      },
    })),
  },
  context: {
    kind: "CASE",
    history: "NONE",
    notes: [],
    payments: [],
    nextActions: [],
    caseId: "019aa6a0-0000-7000-8000-000000000001",
    revision: 1,
    workRevision: 0,
    tasks: ["CASE:CORRECT"],
    editableInputs: [],
    correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP", "UPDATE"] },
    seeds: { paymentCurrency: null, registration: null, decision: null, work: null },
  },
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

it("admits semantic current groups with opaque saved captions and no display-state reconstruction", () => {
  expect(isResponse("case.get", reply(document))).toBe(true);
  expect(document.context.seeds.registration).toBeNull();
});

it("rejects duplicate, unsupported and unoffered current groups", () => {
  expect(
    isResponse(
      "case.get",
      reply({
        ...document,
        screen: {
          ...document.screen,
          currentGroups: [document.screen.currentGroups[0], document.screen.currentGroups[0]],
        },
      }),
    ),
  ).toBe(false);
  expect(
    isResponse(
      "case.get",
      reply({
        ...document,
        screen: {
          ...document.screen,
          currentGroups: [{ ...document.screen.currentGroups[0], group: "payment" }],
        },
      }),
    ),
  ).toBe(false);
  expect(
    isResponse(
      "case.get",
      reply({
        ...document,
        context: {
          ...document.context,
          correction: { ...document.context.correction, decision: ["KEEP"] },
        },
      }),
    ),
  ).toBe(false);
});

it("rejects saved current groups under tightened or erased disclosure independently of seeds", () => {
  expect(isResponse("case.get", reply({ ...document, disclosure: "WITHHELD" }))).toBe(false);
  expect(isResponse("case.get", reply({ ...document, disclosure: "ERASED" }))).toBe(false);
});
