import { operationTask } from "../../src/protocol/taskCatalog";
import type { CurrentCase } from "../../src/protocol/types";
import type { Screen } from "@contracts/client";
import { workBasis } from "./inputs";

/** An authored HUMAN document, independent of the DATA case projection and its presentation policy. */
export const caseIdentityScreen: Screen = {
  regions: [
    {
      role: "IDENTITY",
      content: {
        kind: "ENTRIES",
        entries: [
          {
            key: "caseReference",
            labelId: "field.caseReference.label",
            prominence: "PRIMARY",
            value: { kind: "REFERENCE", value: "CASE-1" },
          },
          {
            key: "claimantName",
            labelId: "field.claimantName.label",
            prominence: "PRIMARY",
            value: { kind: "PARTY", value: "Synthetic claimant" },
          },
          {
            key: "insurerName",
            labelId: "field.insurerName.label",
            prominence: "PRIMARY",
            value: { kind: "PARTY", value: "Synthetic insurer" },
          },
        ],
      },
    },
  ],
};
export const caseSubject: CurrentCase["subject"] = {
  kind: "CASE",
  caseId: "20000000-0000-4000-8000-000000000001",
  revision: 1,
  workRevision: 0,
};
export const caseDocumentInput = (
  overrides: Partial<CurrentCase> = {},
  presentation: Screen = caseIdentityScreen,
): CurrentCase => ({
  subject: caseSubject,
  screen: presentation,
  actions: [],
  links: [],
  nextCursor: null,
  history: "FULL",
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
  ...overrides,
});

/** Fixture authors choose offers explicitly; declaration metadata supplies only their input identities. */
export const registrationInputs = operationTask("case.amendRegistration")
  .inputs.filter((input) => input.source === "AUTHORED" && input.id !== "field:caseReference")
  .map((input) => input.id);
export const decisionInputs = operationTask("case.decide")
  .inputs.filter((input) => input.source === "AUTHORED" && input.id !== "field:caseReference")
  .map((input) => input.id);
export const correctionChoices = {
  registration: ["KEEP", "UPDATE"],
  decision: ["KEEP", "UPDATE", "CLEAR"],
} as const;
