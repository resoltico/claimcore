import { expect, it } from "vitest";
import { isRequest } from "@contracts/client";
import { assembleTask, TaskInputError } from "../../../src/operation/taskForm";
import { taskForAct, taskForCommand, taskInput } from "../../../src/protocol/taskCatalog";
import { isCaseAct } from "../../../src/operation/metadata";

const operationId = "019aa6a0-0000-7000-8000-000000000001";
const target = { kind: "ID", caseId: "00000000-0000-4000-8000-000000000002" } as const;
const principal = {
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "typed-person",
} as const;

it("binds reviewed versions and resource handles outside editable form values", () => {
  const bait = {
    "input:expectedRevision": "999",
    "input:expectedWorkRevision": "999",
    "input:expectedReminderRevision": "999",
    "input:reminderId": "00000000-0000-4000-8000-000000000099",
    "input:noteId": "00000000-0000-4000-8000-000000000099",
    "input:paymentId": "00000000-0000-4000-8000-000000000099",
  };
  const record = assembleTask("CASE:CLOSE", bait, {
    kind: "CASE",
    operationId,
    target,
    revision: 7,
  });
  const work = assembleTask(
    "WORK:UPDATE_WORK",
    { ...bait, "field:handlingSummary": "Exact\nwork" },
    { kind: "WORK", operationId, target, workRevision: 2 },
  );
  const reminder = assembleTask("REMINDER:REMINDER_COMPLETE", bait, {
    kind: "REMINDER",
    operationId,
    target,
    reminder: { reminderId: operationId, revision: 3 },
  });
  const note = assembleTask("NOTE:REMOVE_NOTE", bait, {
    kind: "NOTE",
    operationId,
    target,
    noteId: operationId,
  });
  const payment = assembleTask("CASE:REMOVE_PAYMENT", bait, {
    kind: "CASE",
    operationId,
    target,
    revision: 7,
    paymentId: operationId,
  });
  expect(record.command).toMatchObject({ expectedRevision: 7 });
  expect(work.command).toMatchObject({
    expectedRevision: 2,
    act: { values: { handlingSummary: "Exact\nwork" } },
  });
  expect(reminder.command).toMatchObject({
    act: { kind: "COMPLETE", reminderId: operationId, expectedRevision: 3 },
  });
  expect(note.command).toMatchObject({ act: { kind: "REMOVE_NOTE", noteId: operationId } });
  expect(payment.command).toMatchObject({
    act: { kind: "REMOVE_PAYMENT", paymentId: operationId },
  });
  for (const submission of [record, work, reminder, note, payment]) {
    expect(isRequest("operation.execute", submission.command)).toBe(true);
  }
});

it("preserves selected empty text, explicit clear, omitted fields and correction group intent", () => {
  const result = assembleTask(
    "CASE:CORRECT",
    {
      "group:registration": "UPDATE",
      "group:decision": "CLEAR",
      "field:claimantName": "",
      "field:policyNumber": null,
    },
    { kind: "CASE", operationId, target, revision: 7 },
  );
  expect(result.command).toEqual({
    kind: "CASE",
    operationId,
    target,
    expectedRevision: 7,
    act: {
      kind: "CORRECT",
      groups: {
        registration: { kind: "UPDATE", values: { claimantName: "", policyNumber: null } },
        decision: { kind: "CLEAR" },
      },
    },
  });
});

it("requires both coordinates when a money update is selected", () => {
  expect(() =>
    assembleTask(
      "CASE:AMEND_REGISTRATION",
      { "field:claimedAmount": "12.3400" },
      { kind: "CASE", operationId, target, revision: 7 },
    ),
  ).toThrow(TaskInputError);
  try {
    assembleTask(
      "CASE:AMEND_REGISTRATION",
      { "field:claimedAmount": "12.3400" },
      { kind: "CASE", operationId, target, revision: 7 },
    );
  } catch (error) {
    expect(error).toBeInstanceOf(TaskInputError);
    if (error instanceof TaskInputError) {
      expect(error.input).toBe("field:claimedCurrency");
    }
  }
});

it("freezes the submitted ordinal map while retaining invalid duplicate business rows for Core diagnostics", () => {
  const rows = [
    {
      key: "row-a",
      values: {
        "input:role": "CASE_READER",
        "input:scope": "CASE",
        "field:caseReference": "CASE-A",
      },
    },
    {
      key: "row-b",
      values: {
        "input:role": "CASE_READER",
        "input:scope": "CASE",
        "field:caseReference": "CASE-A",
      },
    },
  ];
  const result = assembleTask(
    "AUTHORITY:ADMIT",
    {
      "input:profile.displayName": "Authored profile",
      authorityGrants: { kind: "COLLECTION", rows },
    },
    { kind: "AUTHORITY", operationId, principal },
  );
  rows.reverse();
  expect(result.submittedRows.authorityGrants).toEqual(["row-a", "row-b"]);
  expect(Object.isFrozen(result.submittedRows)).toBe(true);
  expect(Object.isFrozen(result.submittedRows.authorityGrants)).toBe(true);
  expect(result.command).toMatchObject({
    kind: "AUTHORITY",
    target: principal,
    act: {
      kind: "ADMIT",
      grants: [
        {
          role: "CASE_READER",
          scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "CASE-A" } },
        },
        {
          role: "CASE_READER",
          scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "CASE-A" } },
        },
      ],
    },
  });
  expect(JSON.stringify(result.command)).not.toContain("row-a");
  expect(isRequest("operation.execute", result.command)).toBe(true);
});

it("rejects duplicate interface row keys without guessing which row owns a diagnostic", () => {
  expect(() =>
    assembleTask(
      "AUTHORITY:ADMIT",
      {
        "input:profile.displayName": "Authored profile",
        authorityGrants: {
          kind: "COLLECTION",
          rows: [
            { key: "same", values: { "input:role": "AUDITOR" } },
            { key: "same", values: { "input:role": "AUDITOR" } },
          ],
        },
      },
      { kind: "AUTHORITY", operationId, principal },
    ),
  ).toThrow(TaskInputError);
});

it("maps semantic actions and family-local command tags using declared bindings", () => {
  expect(taskForAct("REMINDER", "REMINDER_COMPLETE").id).toBe("REMINDER:REMINDER_COMPLETE");
  expect(taskForCommand({ kind: "REMINDER", act: { kind: "COMPLETE" } }).id).toBe(
    "REMINDER:REMINDER_COMPLETE",
  );
  expect(taskInput("field:claimantName")).toMatchObject({
    key: "claimantName",
    kind: "FIELD",
    group: "registration",
  });
  expect(isCaseAct("CLOSE")).toBe(true);
  expect(isCaseAct("COMPLETE")).toBe(false);
});

it("preserves exact Unicode narrative input and a work-summary clear without financial fields", () => {
  const exact = "  العربية\n\n😀  ";
  const amended = assembleTask(
    "CASE:AMEND_REGISTRATION",
    { "field:incidentDescription": exact },
    { kind: "CASE", operationId, target, revision: 7 },
  );
  expect(amended.command).toMatchObject({
    act: { kind: "AMEND_REGISTRATION", values: { incidentDescription: exact } },
  });
  const work = assembleTask(
    "WORK:UPDATE_WORK",
    { "field:handlingSummary": null },
    {
      kind: "WORK",
      operationId,
      target: { kind: "REFERENCE", caseReference: "WORK-1" },
      workRevision: 7,
    },
  );
  expect(work.command).toEqual({
    kind: "WORK",
    operationId,
    target: { kind: "REFERENCE", caseReference: "WORK-1" },
    expectedRevision: 7,
    act: { kind: "UPDATE_WORK", values: { handlingSummary: null } },
  });
});
it("keeps ordered admission scope branches and authored references while refusing an incomplete row set", () => {
  const values = {
    "input:profile.displayName": "Authored profile",
    authorityGrants: {
      kind: "COLLECTION" as const,
      rows: [
        {
          key: "owner",
          values: {
            "input:role": "OWNER",
            "input:scope": "INSTALLATION",
            "field:caseReference": "ignored",
          },
        },
        {
          key: "reader",
          values: {
            "input:role": "CASE_READER",
            "input:scope": "CASE",
            "field:caseReference": "  Exact/مرجع  ",
          },
        },
      ],
    },
  };
  const result = assembleTask("AUTHORITY:ADMIT", values, {
    kind: "AUTHORITY",
    operationId,
    principal,
  });
  expect(result.command).toMatchObject({
    act: {
      kind: "ADMIT",
      grants: [
        { role: "OWNER", scope: { kind: "INSTALLATION" } },
        {
          role: "CASE_READER",
          scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "  Exact/مرجع  " } },
        },
      ],
    },
  });
  expect(() =>
    assembleTask(
      "AUTHORITY:ADMIT",
      { ...values, authorityGrants: { kind: "COLLECTION", rows: [] } },
      { kind: "AUTHORITY", operationId, principal },
    ),
  ).toThrow(TaskInputError);
});

it.each([
  ["field:incidentDescription", "incidentDescription"],
  ["field:policyNumber", "policyNumber"],
  ["field:insurerClaimReference", "insurerClaimReference"],
] as const)(
  "keeps empty Set, Clear and omission distinct for new registration %s",
  (input, field) => {
    const authored = {
      "field:caseReference": "EMPTY-SET",
      "field:incidentDate": "2026-11-01",
      "field:incidentNotificationDate": "2026-11-02",
      "field:incidentCountry": "LV",
      "field:claimantName": "Claimant",
      "field:insurerName": "Insurer",
      "field:claimedAmount": "10",
      "field:claimedCurrency": "EUR",
    };
    const expected = {
      incidentDate: "2026-11-01",
      incidentNotificationDate: "2026-11-02",
      incidentCountry: "LV",
      claimantName: "Claimant",
      insurerName: "Insurer",
      claimedAmount: "10",
      claimedCurrency: "EUR",
    };
    for (const value of ["", null, undefined]) {
      const result = assembleTask(
        "CASE:OPEN",
        { ...authored, ...(value === undefined ? {} : { [input]: value }) },
        { kind: "NEW_CASE", operationId },
      );
      expect(result.command).toEqual({
        kind: "CASE",
        operationId,
        target: { kind: "REFERENCE", caseReference: "EMPTY-SET" },
        expectedRevision: 0,
        act: {
          kind: "OPEN",
          values: { ...expected, ...(value === undefined ? {} : { [field]: value }) },
        },
      });
      expect(isRequest("operation.preview", result.command)).toBe(true);
    }
  },
);

it.each(["AUTHORITY:REGISTER", "AUTHORITY:SET_PROFILE"] as const)(
  "preserves authored empty contact, Clear and omission for %s",
  (task) => {
    for (const contact of ["", null, undefined]) {
      const result = assembleTask(
        task,
        {
          "input:profile.displayName": "Profile",
          ...(contact === undefined ? {} : { "input:profile.contact": contact }),
        },
        { kind: "AUTHORITY", operationId, principal },
      );
      expect(result.command).toEqual({
        kind: "AUTHORITY",
        operationId,
        target: principal,
        act: {
          kind: task === "AUTHORITY:REGISTER" ? "REGISTER" : "SET_PROFILE",
          profile: { displayName: "Profile", ...(contact === undefined ? {} : { contact }) },
        },
      });
      expect(isRequest("operation.preview", result.command)).toBe(true);
    }
  },
);

it.each(["GRANT", "REVOKE"] as const)(
  "submits the visible blank case-scope reference for Core refusal on %s",
  (kind) => {
    const binding = { kind: "AUTHORITY", operationId, principal } as const;
    const values = { "input:role": "CASE_READER", "input:scope": "CASE" };
    const result = assembleTask(`AUTHORITY:${kind}`, values, binding);
    expect(result.command).toEqual({
      kind: "AUTHORITY",
      operationId,
      target: principal,
      act: {
        kind,
        role: "CASE_READER",
        scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" } },
      },
    });
    expect(isRequest("operation.preview", result.command)).toBe(true);
    expect(() =>
      assembleTask(`AUTHORITY:${kind}`, { ...values, "field:caseReference": null }, binding),
    ).toThrow(TaskInputError);
  },
);

it("retains an unedited case-scoped admission row for ordinal-correlated Core refusal", () => {
  const result = assembleTask(
    "AUTHORITY:ADMIT",
    {
      "input:profile.displayName": "Candidate",
      authorityGrants: {
        kind: "COLLECTION",
        rows: [
          { key: "unentered-case", values: { "input:role": "CASE_READER", "input:scope": "CASE" } },
        ],
      },
    },
    { kind: "AUTHORITY", operationId, principal },
  );
  expect(result.command).toEqual({
    kind: "AUTHORITY",
    operationId,
    target: principal,
    act: {
      kind: "ADMIT",
      profile: { displayName: "Candidate" },
      grants: [
        {
          role: "CASE_READER",
          scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" } },
        },
      ],
    },
  });
  expect(result.submittedRows.authorityGrants).toEqual(["unentered-case"]);
  expect(isRequest("operation.preview", result.command)).toBe(true);
});
