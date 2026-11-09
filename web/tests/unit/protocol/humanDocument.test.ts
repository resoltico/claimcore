import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const registration = {
  incidentDate: "2026-01-01",
  incidentNotificationDate: "2026-01-02",
  incidentCountry: "LV",
  claimantName: "Editable input bait",
  insurerName: "Synthetic insurer",
  claimedAmount: "100",
  claimedCurrency: "EUR",
};
const editable = [
  "field:incidentDate",
  "field:incidentNotificationDate",
  "field:incidentCountry",
  "field:incidentDescription",
  "field:policyNumber",
  "field:insurerClaimReference",
  "field:claimantName",
  "field:insurerName",
  "field:claimedAmount",
  "field:claimedCurrency",
];
const context = {
  kind: "CASE",
  caseId: "019aa6a0-0000-7000-8000-000000000001",
  revision: 1,
  workRevision: 0,
  tasks: ["CASE:AMEND_REGISTRATION"],
  editableInputs: editable,
  correction: null,
  seeds: { registration, decision: null, work: null },
};
const screen = {
  regions: [
    {
      role: "FACTS",
      content: {
        kind: "ENTRIES",
        entries: [
          {
            key: "opaque-saved-fact",
            labelId: "label.details",
            prominence: "PRIMARY",
            value: { kind: "TEXT", value: "Core saved caption Ω" },
          },
        ],
      },
    },
  ],
};
const document = {
  screen,
  context,
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

it("admits an authored opaque document and an explicitly permitted input seed without conflating their values", () => {
  expect(isResponse("case.get", reply(document))).toBe(true);
  expect(isResponse("case.get", reply({ ...document, context: { ...context, tasks: [] } }))).toBe(
    false,
  );
});

it("rejects erased seed disclosure even if all child scalar shapes are valid", () => {
  expect(isResponse("case.get", reply({ ...document, disclosure: "ERASED" }))).toBe(false);
});

it("rejects action references that are absent from the declared handles", () => {
  const unbound = {
    ...document,
    screen: {
      regions: [
        {
          role: "RECOMMENDATION",
          content: { kind: "ACTIONS", recommended: "ERASE", groups: [] },
        },
      ],
    },
  };
  expect(isResponse("case.get", reply(unbound))).toBe(false);
});
