import { expect, it } from "vitest";
import {
  assembleAuthorityAdmission,
  assembleCorrection,
  type CorrectionTaskDraft,
  assembleRecordPayment,
  assembleRegistrationAmendment,
  emptyRegistrationAmendmentDraft,
  type AuthorityAdmissionRow,
} from "@contracts/client";

const basis = {
  operationId: "019aa6a0-0000-7000-8000-000000000001",
  target: { kind: "REFERENCE", caseReference: "TASK-1" },
  expectedRevision: 1,
} as const;

it("keeps omission, explicit Clear and authored empty Set distinct while selecting money atomically", () => {
  const draft = emptyRegistrationAmendmentDraft();
  const command = assembleRegistrationAmendment(basis, {
    ...draft,
    claimantName: { kind: "SET", value: "" },
    incidentDescription: { kind: "CLEAR" },
    claimed: { kind: "SET", value: { amount: "12.3400", currency: "EUR" } },
  });
  expect(command).toEqual({
    kind: "CASE",
    ...basis,
    act: {
      kind: "AMEND_REGISTRATION",
      values: {
        claimantName: "",
        incidentDescription: null,
        claimedAmount: "12.3400",
        claimedCurrency: "EUR",
      },
    },
  });
  expect(draft.claimantName).toEqual({ kind: "KEEP" });
});

it("preserves exact payment text without a second financial admission engine", () => {
  expect(
    assembleRecordPayment(basis, {
      paymentDate: "2026-11-01",
      paid: { amount: "0.0001", currency: "KWD" },
    }),
  ).toEqual({
    kind: "CASE",
    ...basis,
    act: {
      kind: "RECORD_PAYMENT",
      values: { paymentDate: "2026-11-01", paidAmount: "0.0001", paidCurrency: "KWD" },
    },
  });
});

it("freezes the submitted ordinal map even for duplicate invalid rows and excludes keys from business content", () => {
  const grant = {
    role: "CASE_READER",
    scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" } },
  } as const;
  const rows: AuthorityAdmissionRow[] = [
    { key: "local-b", grant },
    { key: "local-a", grant },
  ];
  const target = {
    kind: "HUMAN",
    issuer: "https://issuer.example/",
    subject: "task-person",
  } as const;
  const profile = { displayName: "Task person" };
  const original = assembleAuthorityAdmission(basis.operationId, target, profile, rows);
  rows.reverse();
  const reordered = assembleAuthorityAdmission(basis.operationId, target, profile, rows);
  expect(original.submittedRows.authorityGrants).toEqual(["local-b", "local-a"]);
  expect(reordered.submittedRows.authorityGrants).toEqual(["local-a", "local-b"]);
  expect(Object.isFrozen(original.submittedRows.authorityGrants)).toBe(true);
  expect(original.command).toEqual(reordered.command);
  expect(original.command).toEqual({
    kind: "AUTHORITY",
    operationId: basis.operationId,
    target,
    act: { kind: "ADMIT", profile, grants: [grant, grant] },
  });
  expect(JSON.stringify(original.command)).not.toContain("local-");
});

it("keeps correction group Keep, Clear and Update explicit without inventing values", () => {
  const selection: ReadonlyArray<readonly [CorrectionTaskDraft["decision"], unknown]> = [
    [{ kind: "KEEP" }, { kind: "KEEP" }],
    [{ kind: "CLEAR" }, { kind: "CLEAR" }],
    [
      {
        kind: "UPDATE",
        draft: {
          paymentDecisionDate: { kind: "SET", value: "" },
          payable: { kind: "SET", value: { amount: "10.00", currency: "EUR" } },
        },
      },
      {
        kind: "UPDATE",
        values: { paymentDecisionDate: "", payableAmount: "10.00", payableCurrency: "EUR" },
      },
    ],
  ];
  for (const [decision, expected] of selection) {
    expect(assembleCorrection(basis, { registration: { kind: "KEEP" }, decision })).toEqual({
      kind: "CASE",
      ...basis,
      act: { kind: "CORRECT", groups: { registration: { kind: "KEEP" }, decision: expected } },
    });
  }
});
