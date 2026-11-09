import { expect, expectTypeOf, it } from "vitest";
import {
  assembleAuthorityAdmission,
  assembleCaseOperation,
  assembleAuthorityOperation,
  type CaseAct_OPEN,
  type CaseAct_RECORD_PAYMENT,
  type AuthorityAct_ADMIT,
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

it("publishes finite operation task identity and presentation metadata without transport paths", async () => {
  const { taskDefinitions, taskInputDefinitions } = await import("@contracts/client");
  const operations = taskDefinitions.filter((task) => task.kind === "OPERATION");
  expect(operations).toHaveLength(29);
  expect(operations.find((task) => task.id === "CASE:ERASE")).toMatchObject({
    subject: "CASE",
    act: "ERASE",
    consequence: "IRREVERSIBLE",
    endpoint: "operation.execute",
  });
  expect(taskDefinitions.find((task) => task.id === "query:operationGet")?.inputs).toEqual([
    { id: "input:operationId", mode: "REQUIRED", source: "AUTHORED", choices: [] },
  ]);
  expect(operations.find((task) => task.id === "AUTHORITY:ADMIT")?.collections).toMatchObject([
    { id: "authorityGrants", minimumRows: 1, maximumRows: 48 },
  ]);
  expect(taskInputDefinitions.find((input) => input.id === "field:claimantName")).toMatchObject({
    key: "claimantName",
    kind: "FIELD",
    group: "registration",
    labelId: "field.claimantName.label",
  });
  expect(JSON.stringify(taskDefinitions)).not.toContain('"path"');
  expect(JSON.stringify(taskDefinitions)).not.toContain('"branches"');
});

it("assembles case creation with its known initial revision and preserves a UUID-shaped authored reference", async () => {
  const { assembleOpenCase, isRequest } = await import("@contracts/client");
  const registration = {
    incidentDate: "2026-11-08",
    incidentNotificationDate: "2026-11-09",
    incidentCountry: "LV",
    claimantName: "Authored claimant",
    insurerName: "Authored insurer",
    claimedAmount: "1.2300",
    claimedCurrency: "EUR",
  };
  const reference = "00000000-0000-4000-8000-000000000002";
  const command = assembleOpenCase(basis.operationId, reference, registration);
  expect(command).toEqual({
    kind: "CASE",
    operationId: basis.operationId,
    target: { kind: "REFERENCE", caseReference: reference },
    expectedRevision: 0,
    act: { kind: "OPEN", values: registration },
  });
  expect(isRequest("operation.execute", command)).toBe(true);
});

it("assembles remaining subjects with explicit resource identities and the correct version lifetime", async () => {
  const {
    assembleCaseOperation,
    assembleWorkOperation,
    assembleNoteOperation,
    assembleReminderOperation,
    assembleAuthorityOperation,
    isRequest,
  } = await import("@contracts/client");
  const target = { kind: "ID", caseId: "00000000-0000-4000-8000-000000000002" } as const;
  const caseCommand = assembleCaseOperation(
    { ...basis, target, expectedRevision: 7 },
    { kind: "CLOSE" },
  );
  const work = assembleWorkOperation(
    { operationId: basis.operationId, target, workRevision: 2 },
    {
      kind: "UPDATE_WORK",
      values: { handlingSummary: "Exact\nwork text", responsibleActor: { kind: "CLEAR" } },
    },
  );
  const note = assembleNoteOperation(
    { operationId: basis.operationId, target },
    { kind: "ADD_NOTE", text: "Exact\nnote text" },
  );
  const reminder = assembleReminderOperation(
    { operationId: basis.operationId, target },
    {
      kind: "COMPLETE_AND_PLAN_NEXT",
      reminderId: "00000000-0000-4000-8000-000000000003",
      expectedRevision: 3,
      action: "Authored next action",
      dueDate: "2026-11-10",
    },
  );
  const authority = assembleAuthorityOperation(
    basis.operationId,
    { kind: "HUMAN", issuer: "https://issuer.example/", subject: "typed-person" },
    { kind: "ENABLE" },
  );
  expect(caseCommand.expectedRevision).toBe(7);
  expect(work).toEqual({
    kind: "WORK",
    operationId: basis.operationId,
    target,
    expectedRevision: 2,
    act: {
      kind: "UPDATE_WORK",
      values: { handlingSummary: "Exact\nwork text", responsibleActor: { kind: "CLEAR" } },
    },
  });
  expect(note.act).toEqual({ kind: "ADD_NOTE", text: "Exact\nnote text" });
  expect(reminder.act).toEqual({
    kind: "COMPLETE_AND_PLAN_NEXT",
    reminderId: "00000000-0000-4000-8000-000000000003",
    expectedRevision: 3,
    action: "Authored next action",
    dueDate: "2026-11-10",
  });
  expect(authority).toEqual({
    kind: "AUTHORITY",
    operationId: basis.operationId,
    target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "typed-person" },
    act: { kind: "ENABLE" },
  });
  for (const command of [caseCommand, work, note, reminder, authority]) {
    expect(isRequest("operation.execute", command)).toBe(true);
  }
});

it("keeps creation, atomic money updates and correlated admission on their dedicated typed paths", () => {
  expectTypeOf<CaseAct_OPEN>().not.toMatchTypeOf<Parameters<typeof assembleCaseOperation>[1]>();
  expectTypeOf<CaseAct_RECORD_PAYMENT>().not.toMatchTypeOf<
    Parameters<typeof assembleCaseOperation>[1]
  >();
  expectTypeOf<AuthorityAct_ADMIT>().not.toMatchTypeOf<
    Parameters<typeof assembleAuthorityOperation>[2]
  >();
});
