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
export const closeCaseScreen: Screen = {
  regions: [
    ...caseIdentityScreen.regions,
    { role: "RECOMMENDATION", content: { kind: "ACTIONS", recommended: "case.close", groups: [] } },
  ],
};
export const emptyCaseSeeds: CurrentCase["context"]["seeds"] = {
  registration: null,
  decision: null,
  paymentCurrency: null,
  work: null,
};
const context: CurrentCase["context"] = {
  kind: "CASE",
  caseId: "20000000-0000-4000-8000-000000000001",
  revision: 1,
  workRevision: 0,
  history: "FULL",
  tasks: [],
  editableInputs: [],
  correction: null,
  seeds: emptyCaseSeeds,
  notes: [],
  payments: [],
  nextActions: [],
};
export const caseDocumentInput = (
  binding: Partial<CurrentCase["context"]> = {},
  presentation: Screen = caseIdentityScreen,
): CurrentCase => ({
  screen: presentation,
  context: { ...context, ...binding },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
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
