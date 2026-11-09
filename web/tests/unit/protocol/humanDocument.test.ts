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

const reviewContext = {
  kind: "REVIEW",
  task: "AUTHORITY:ERASE_PROFILE",
  target: {
    kind: "AUTHORITY",
    actorId: "019aa6a0-0000-7000-8000-000000000002",
    scope: { kind: "INSTALLATION" },
  },
  inputs: [
    "input:operationId",
    "input:principal.kind",
    "input:principal.issuer",
    "input:principal.subject",
  ],
  consequence: "IRREVERSIBLE",
  confirmation: { kind: "OPERATION", operationId: "019aa6a0-0000-7000-8000-000000000003" },
};

it("checks review control bindings against the declared task without reconstructing saved facts", () => {
  const review = { ...document, context: reviewContext };
  expect(isResponse("case.get", reply(review))).toBe(true);
  for (const alteredContext of [
    { ...reviewContext, task: "query:context" },
    { ...reviewContext, consequence: "ROUTINE" },
    { ...reviewContext, inputs: [] },
    { ...reviewContext, inputs: [...reviewContext.inputs, reviewContext.inputs[0]] },
    {
      ...reviewContext,
      target: {
        kind: "CASE",
        caseId: "019aa6a0-0000-7000-8000-000000000002",
        beforeRevision: null,
      },
    },
    { ...reviewContext, confirmation: { kind: "CASE_REFERENCE", reference: "UNRELATED" } },
  ]) {
    expect(isResponse("case.get", reply({ ...review, context: alteredContext }))).toBe(false);
  }
});

it("retains compound receipt handles while rejecting duplicate effect identities", () => {
  const version = { reminderId: "019aa6a0-0000-7000-8000-000000000004", revision: 2 };
  const receipt = {
    ...document,
    disclosure: "PARTIAL",
    context: {
      kind: "RECEIPT",
      target: {
        kind: "REMINDER",
        caseId: "019aa6a0-0000-7000-8000-000000000002",
        versions: [version, { reminderId: "019aa6a0-0000-7000-8000-000000000005", revision: 1 }],
      },
    },
  };
  expect(isResponse("case.get", reply(receipt))).toBe(true);
  expect(
    isResponse(
      "case.get",
      reply({
        ...receipt,
        context: {
          ...receipt.context,
          target: { ...receipt.context.target, versions: [version, version] },
        },
      }),
    ),
  ).toBe(false);
});
