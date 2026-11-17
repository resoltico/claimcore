import { formValueText } from "../../../src/protocol/forms";
import { createDraft } from "../../../src/operation/operationDraft";
import { initialAffordanceValues } from "../../../src/operation/affordances";
import { operationAffordance } from "../../support/affordances.fixtures";
import { expect, it } from "vitest";
import { assembleTask } from "../../../src/operation/taskForm";
import { taskForCommand, taskInput } from "../../../src/protocol/taskCatalog";
import { isCaseAct } from "../../../src/operation/metadata";
const operationId = "019aa6a0-0000-7000-8000-000000000001";
const target = { kind: "ID", caseId: "00000000-0000-4000-8000-000000000002" } as const;
it("keeps reviewed selection and version outside authored inputs", () => {
  const value = assembleTask(
    "case.removePayment",
    {
      "input:expectedRevision": "999",
      "input:paymentId": "foreign",
      "field:policyNumber": "foreign",
    },
    { kind: "CASE", target, expectedRevision: 7, paymentId: operationId },
    operationId,
  );
  expect(value.command).toEqual({
    operationId,
    task: "case.removePayment",
    binding: { kind: "CASE", target, expectedRevision: 7, paymentId: operationId },
    inputs: {},
  });
});
it("preserves authored tokens, omission, null, empty text and exact money", () => {
  const value = assembleTask(
    "case.amendRegistration",
    {
      "field:claimantName": "",
      "field:incidentDescription": null,
      "field:claimedAmount": "12.3400",
      "field:claimedCurrency": "EUR",
    },
    { kind: "CASE", target, expectedRevision: 1 },
    operationId,
  );
  expect(value.command.inputs).toEqual({
    "field:claimantName": "",
    "field:incidentDescription": null,
    "field:claimedAmount": "12.3400",
    "field:claimedCurrency": "EUR",
  });
  expect(value.command.inputs).not.toHaveProperty("field:policyNumber");
});
it("retains work omission and explicit clearing without a second intent vocabulary", () => {
  const value = assembleTask(
    "work.update",
    { "field:responsibleActor": null },
    { kind: "WORK", target, expectedRevision: 2 },
    operationId,
  );
  expect(value.command.inputs).toEqual({ "field:responsibleActor": null });
});
it("freezes submitted row ordinals separately from the business map", () => {
  const rows = [
    { key: "row-a", values: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" } },
    { key: "row-b", values: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" } },
  ];
  const value = assembleTask(
    "people.admit",
    {
      peopleGrants: { kind: "COLLECTION", rows },
      "input:principal.kind": "HUMAN",
      "input:principal.issuer": "https://issuer.example/",
      "input:principal.subject": "person",
      "input:profile.displayName": "Person",
    },
    { kind: "PEOPLE", expectedRevision: 0 },
    operationId,
  );
  expect(value.submittedRows.peopleGrants).toEqual(["row-a", "row-b"]);
  expect(Object.isFrozen(value.submittedRows.peopleGrants)).toBe(true);
  expect(
    value.command.task === "people.admit" ? value.command.inputs["input:grants"] : null,
  ).toEqual([
    { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
    { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
  ]);
  rows.reverse();
  expect(value.submittedRows.peopleGrants).toEqual(["row-a", "row-b"]);
});
it("uses task identities and declarations without a field mapping table", () => {
  expect(taskForCommand({ task: "case.open" }).id).toBe("case.open");
  expect(taskInput("field:policyNumber").key).toBe("policyNumber");
  expect(isCaseAct("case.recordPayment")).toBe(true);
  expect(isCaseAct("people.disable")).toBe(false);
});

it("rejects duplicate local row keys before correlating diagnostics", () => {
  const rows = [
    { key: "same", values: {} },
    { key: "same", values: {} },
  ];
  expect(() =>
    assembleTask(
      "people.admit",
      { peopleGrants: { kind: "COLLECTION", rows } },
      { kind: "PEOPLE", expectedRevision: 0 },
      operationId,
    ),
  ).toThrow("row keys must be unique");
});
it("keeps a UUID-shaped authored case reference as a reference", () => {
  const value = assembleTask(
    "case.open",
    { "field:caseReference": operationId },
    { kind: "CASE", expectedRevision: 0 },
    operationId,
  );
  expect(value.command.binding).toEqual({
    kind: "CASE",
    expectedRevision: 0,
  });
  expect(value.command.inputs).toEqual({ "field:caseReference": operationId });
});

it("submits untouched required text controls as empty text for Core field advice", () => {
  const action = operationAffordance(
    "people.register",
    { kind: "PEOPLE", expectedRevision: 0 },
    {
      "input:principal.kind": "HUMAN",
      "input:principal.issuer": "https://issuer.example/",
    },
  );
  const values = initialAffordanceValues(action);
  expect(values["input:principal.subject"]).toBe("");
  expect(values["input:profile.displayName"]).toBe("");
  expect(values).not.toHaveProperty("input:profile.contact");
  const submitted = assembleTask("people.register", values, action.binding, operationId);
  expect(submitted.command.inputs).toMatchObject({
    "input:principal.subject": "",
    "input:profile.displayName": "",
  });
});

it("serializes exactly one active raw grant target, retaining blank and malformed text", () => {
  const binding = { kind: "PEOPLE", actorId: operationId, expectedRevision: 3 } as const;
  for (const [selection, expected] of [
    [{ kind: "INSTALLATION" }, { "input:scope": "INSTALLATION" }],
    [
      { kind: "REFERENCE", text: operationId },
      { "input:scope": "CASE", "field:caseReference": operationId },
    ],
    [
      { kind: "REFERENCE", text: "" },
      { "input:scope": "CASE", "field:caseReference": "" },
    ],
    [
      { kind: "ID", text: "bad-id" },
      { "input:scope": "CASE", "input:caseId": "bad-id" },
    ],
    [
      { kind: "ID", text: "" },
      { "input:scope": "CASE", "input:caseId": "" },
    ],
  ] as const) {
    const value = assembleTask(
      "people.grant",
      {
        "input:role": "CASE_READER",
        "input:scope": { kind: "GRANT_TARGET", selection },
        "input:caseId": "inactive-id",
        "field:caseReference": "inactive-reference",
      },
      binding,
      operationId,
    );
    expect(value.command.inputs).toEqual({ "input:role": "CASE_READER", ...expected });
    expect(value.command.binding).toEqual(binding);
  }
});

it("preserves the exact fixed Core-seeded ID target, role, actor and reviewed version", () => {
  const binding = { kind: "PEOPLE", actorId: operationId, expectedRevision: 9 } as const;
  const action = operationAffordance(
    "people.revoke",
    binding,
    { "input:role": "CASE_READER", "input:scope": "CASE", "input:caseId": target.caseId },
    [],
  );
  const draft = createDraft(
    { subject: "PEOPLE", kind: "people.revoke" },
    operationId,
    binding,
    initialAffordanceValues(action),
    null,
    action,
  );
  expect(draft.values["input:scope"]).toEqual({
    kind: "GRANT_TARGET",
    selection: { kind: "ID", text: target.caseId },
  });
  const value = assembleTask("people.revoke", draft.values, draft.binding, operationId);
  expect(value.command).toEqual({
    operationId,
    task: "people.revoke",
    binding,
    inputs: { "input:role": "CASE_READER", "input:scope": "CASE", "input:caseId": target.caseId },
  });
  expect(action.editable).toEqual([]);
});

it("keeps existing scalar-list text while structured grant drafts stay outside scalar controls", () => {
  expect(formValueText(["first", "second"])).toBe("first,second");
  expect(formValueText(null)).toBe("");
  expect(formValueText(undefined)).toBe("");
  expect(
    formValueText({ kind: "GRANT_TARGET", selection: { kind: "REFERENCE", text: "PRIVATE" } }),
  ).toBe("");
});

it("fixed Core inputs win while the explicit reviewed binding stays unchanged", () => {
  const action = operationAffordance(
    "people.grant",
    { kind: "PEOPLE", actorId: "10000000-0000-4000-8000-000000000001", expectedRevision: 2 },
    { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
    [],
  );
  const value = assembleTask(
    "people.grant",
    { "input:role": "OWNER", "input:scope": "CASE", "field:caseReference": "OTHER" },
    { kind: "PEOPLE", actorId: "10000000-0000-4000-8000-000000000099", expectedRevision: 9 },
    operationId,
    action,
  );
  expect(value.command.binding).toEqual({
    kind: "PEOPLE",
    actorId: "10000000-0000-4000-8000-000000000099",
    expectedRevision: 9,
  });
  expect(value.command.inputs).toMatchObject({
    "input:role": "CASE_READER",
    "input:scope": "INSTALLATION",
  });
  expect(value.command.inputs).not.toHaveProperty("field:caseReference");
});
it("a noneditable UPDATE seed remains dormant during a closed-case summary edit", () => {
  const binding = { kind: "WORK" as const, target, expectedRevision: 4 };
  const action = operationAffordance(
    "work.update",
    binding,
    {
      "field:responsibleActor": "10000000-0000-4000-8000-000000000099",
      "field:handlingSummary": "Saved summary",
    },
    ["field:handlingSummary"],
  );
  expect(initialAffordanceValues(action)).not.toHaveProperty("field:responsibleActor");
  const submission = assembleTask(
    "work.update",
    { "field:handlingSummary": "Updated summary" },
    binding,
    operationId,
    action,
  );
  expect(submission.command.inputs).toEqual({ "field:handlingSummary": "Updated summary" });
});
