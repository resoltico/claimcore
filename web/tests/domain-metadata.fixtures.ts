import { expect } from "vitest";
import type { CaseView, SemanticDefinition } from "../src/api/v3";
import {
  commandFor,
  correctionGroupName,
  createDraft,
  isCorrectionGroupName,
  isCorrectionValues,
  isDirty,
  prefilledValues,
  type DraftValues,
} from "../src/domain/metadata";
import { freezeRequest } from "../src/domain/operationRequest";
import { caseFields, groupedCorrectionValues } from "./v3-foundation.fixtures";

const replaceField = {
  fieldName: "claimantName",
  prefill: "CURRENT_FIELD",
  currentField: "claimantName",
};
const blankField = { fieldName: "caseReference", prefill: "BLANK" };
const definition = {
  fields: [],
  commands: [
    { kind: "OPEN", inputs: { kind: "FIELDS", fields: [blankField, replaceField] } },
    {
      kind: "CORRECT_CASE",
      inputs: {
        kind: "CORRECTION_GROUPS",
        groups: [{ name: "registration", replaceFields: [replaceField, blankField] }],
      },
    },
  ],
} as unknown as SemanticDefinition;
const current = { fields: caseFields, revision: "1" } as unknown as CaseView;
const keep = { mode: "KEEP" as const, values: {} };
const emptyRegistration = {
  incidentDate: "",
  incidentNotificationDate: "",
  incidentCountry: "",
  claimantName: "",
  insurerName: "",
  claimedAmount: "",
  claimedCurrency: "",
};

const expectGroupNames = () => {
  for (const name of ["registration", "decision", "payment"]) {
    expect(isCorrectionGroupName(name)).toBe(true);
    expect(correctionGroupName(name)).toBe(name);
  }
  expect(isCorrectionGroupName("other")).toBe(false);
  expect(() => correctionGroupName("other")).toThrow(/Unknown correction group other\./u);
  expect(isCorrectionValues({ registration: keep, decision: keep, payment: keep })).toBe(true);
  expect(isCorrectionValues({ registration: keep, decision: keep } as unknown as DraftValues)).toBe(
    false,
  );
  expect(isCorrectionValues({ decision: keep, payment: keep } as unknown as DraftValues)).toBe(
    false,
  );
  expect(isCorrectionValues({ registration: keep, payment: keep } as unknown as DraftValues)).toBe(
    false,
  );
};

const expectPrefill = () => {
  expect(prefilledValues(definition, "OPEN", current)).toEqual({
    caseReference: "",
    claimantName: caseFields.claimantName,
  });
  expect(prefilledValues(definition, "OPEN", null)).toEqual({
    caseReference: "",
    claimantName: "",
  });
  expect(prefilledValues(definition, "CORRECT_CASE", current)).toEqual({
    registration: {
      mode: "KEEP",
      values: { claimantName: caseFields.claimantName, caseReference: "" },
    },
  });
  expect(() => prefilledValues(definition, "CORRECT_CASE", null)).not.toThrow();
  expect(() => commandFor(definition, "CLOSE")).toThrow(/did not describe CLOSE/u);
};

const expectDirtiness = () => {
  expect(isDirty({ a: "" })).toBe(false);
  expect(isDirty({ a: "x" })).toBe(true);
  expect(isDirty({ a: "", b: "x" })).toBe(true);
  expect(isDirty({ registration: keep, decision: keep, payment: keep })).toBe(false);
  expect(
    isDirty({ registration: keep, decision: keep, payment: { mode: "CLEAR", values: {} } }),
  ).toBe(true);
};

const expectFrozenRequests = () => {
  const flat = createDraft("op", "CASE-1", "1", "OPEN", { claimantName: "A" });
  const frozenFlat = freezeRequest(flat);
  expect(frozenFlat).toEqual(flat);
  expect(frozenFlat.command).not.toBe(flat.command);
  const grouped = createDraft("op", "CASE-1", "1", "CORRECT_CASE", groupedCorrectionValues);
  const frozen = freezeRequest(grouped);
  if (frozen.command.kind !== "CORRECT_CASE" || grouped.command.kind !== "CORRECT_CASE") {
    throw new Error("Expected grouped correction requests.");
  }
  expect(frozen).toEqual(grouped);
  expect(frozen.command.groups.registration).not.toBe(grouped.command.groups.registration);
  expect(frozen.command.groups.decision).toEqual({ mode: "KEEP" });
  expect(frozen.command.groups.decision).not.toBe(grouped.command.groups.decision);
};

const expectRegistrationDefaults = () => {
  const draft = createDraft("op", "CASE-1", "1", "CORRECT_CASE", {
    registration: { mode: "REPLACE", values: {} },
    decision: keep,
    payment: keep,
  });
  if (draft.command.kind !== "CORRECT_CASE") {
    throw new Error("Expected a grouped correction request.");
  }
  expect(draft.command.groups.registration).toEqual({ mode: "REPLACE", values: emptyRegistration });
};

/** Metadata helpers and request freezing must behave exactly, not merely plausibly. */
export const expectDomainHelpersExact = () => {
  expectGroupNames();
  expectPrefill();
  expectDirtiness();
  expectFrozenRequests();
  expectRegistrationDefaults();
};
