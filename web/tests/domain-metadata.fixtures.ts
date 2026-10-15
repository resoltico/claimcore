import { expect } from "vitest";
import type { CurrentCase, Descriptors } from "../src/api/service";
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
import { caseFields, groupedCorrectionValues } from "./service-foundation.fixtures";

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
} as unknown as Descriptors;
const current: CurrentCase = {
  caseReference: caseFields.caseReference,
  revision: 1,
  state: { tag: "LIVE", disposition: "ACTIVE", fields: caseFields, availableActs: [] },
};
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
  const baseline = { a: "prefilled" };
  expect(isDirty({ a: "prefilled" }, baseline)).toBe(false);
  expect(isDirty({ a: "" }, baseline)).toBe(true);
  expect(isDirty({ a: "1.0000" }, { a: "1" })).toBe(true);
  expect(isDirty({ a: "" }, { a: "" })).toBe(false);
  expect(isDirty({ a: "x" }, { a: "" })).toBe(true);
  expect(isDirty({ a: "", b: "x" }, { a: "" })).toBe(true);
  expect(isDirty({}, { a: "" })).toBe(true);
  const groups = { registration: keep, decision: keep, payment: keep };
  expect(isDirty(groups, groups)).toBe(false);
  expect(isDirty({ ...groups, payment: { mode: "CLEAR", values: {} } }, groups)).toBe(true);
  expect(
    isDirty({ ...groups, registration: { ...keep, values: { claimantName: "edited" } } }, groups),
  ).toBe(true);
  expect(isDirty(groups, {})).toBe(true);
  expect(isDirty({}, groups)).toBe(true);
};

const expectFrozenRequests = () => {
  const flat = createDraft("op", "CASE-1", 1, "OPEN", { claimantName: "A" });
  const frozenFlat = freezeRequest(flat);
  expect(frozenFlat).toEqual(flat);
  expect(frozenFlat.act).not.toBe(flat.act);
  const grouped = createDraft("op", "CASE-1", 1, "CORRECT_CASE", groupedCorrectionValues);
  const frozen = freezeRequest(grouped);
  if (frozen.act.kind !== "CORRECT_CASE" || grouped.act.kind !== "CORRECT_CASE") {
    throw new Error("Expected grouped correction requests.");
  }
  expect(frozen).toEqual(grouped);
  expect(frozen.act.groups.registration).not.toBe(grouped.act.groups.registration);
  expect(frozen.act.groups.decision).toEqual({ mode: "KEEP" });
  expect(frozen.act.groups.decision).not.toBe(grouped.act.groups.decision);
};

const expectRegistrationDefaults = () => {
  const draft = createDraft("op", "CASE-1", 1, "CORRECT_CASE", {
    registration: { mode: "REPLACE", values: {} },
    decision: keep,
    payment: keep,
  });
  if (draft.act.kind !== "CORRECT_CASE") {
    throw new Error("Expected a grouped correction request.");
  }
  expect(draft.act.groups.registration).toEqual({ mode: "REPLACE", values: emptyRegistration });
};

/** Metadata helpers and request freezing must behave exactly, not merely plausibly. */
export const expectDomainHelpersExact = () => {
  expectGroupNames();
  expectPrefill();
  expectDirtiness();
  expectFrozenRequests();
  expectRegistrationDefaults();
};
