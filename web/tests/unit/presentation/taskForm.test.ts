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
    { kind: "PEOPLE" },
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
      { kind: "PEOPLE" },
      operationId,
    ),
  ).toThrow("row keys must be unique");
});
it("keeps a UUID-shaped authored case reference as a reference", () => {
  const value = assembleTask(
    "case.open",
    { "field:caseReference": operationId },
    { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" }, expectedRevision: 0 },
    operationId,
  );
  expect(value.command.binding).toEqual({
    kind: "CASE",
    target: { kind: "REFERENCE", caseReference: operationId },
    expectedRevision: 0,
  });
});
