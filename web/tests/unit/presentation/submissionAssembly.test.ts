import { expect, it } from "vitest";
import {
  initialSubmissionBinding,
  submissionDefaults,
  mergeSubmissionInputs,
} from "../../../src/operation/submissionAssembly";
import { operationTask } from "../../../src/protocol/taskCatalog";
import { grantTarget } from "../../../src/operation/grantTargetDraft";
import { createDraft } from "../../../src/operation/operationDraft";
import { assembleTask } from "../../../src/operation/taskForm";

const id = "10000000-0000-7000-8000-000000000001";
const selection = { kind: "ID", caseId: id } as const;
const binding = { kind: "PEOPLE", actorId: id, expectedRevision: 3 } as const;

it.each([
  ["case.open", {}, { kind: "CASE", expectedRevision: 0 }],
  ["people.register", {}, { kind: "PEOPLE", expectedRevision: 0 }],
  [
    "case.close",
    { "input:expectedRevision": 3 },
    { kind: "CASE", target: selection, expectedRevision: 3 },
  ],
  [
    "work.update",
    { "input:expectedWorkRevision": 0 },
    { kind: "WORK", target: selection, expectedRevision: 0 },
  ],
  ["note.add", {}, { kind: "NOTE", target: selection }],
  ["note.remove", { "input:noteId": id }, { kind: "NOTE", target: selection, noteId: id }],
  ["reminder.create", {}, { kind: "REMINDER", target: selection }],
  [
    "reminder.update",
    { "input:reminderId": id, "input:expectedReminderRevision": 2 },
    { kind: "REMINDER", target: selection, reminderId: id, expectedRevision: 2 },
  ],
  [
    "case.removePayment",
    { "input:paymentId": id, "input:expectedRevision": 3 },
    { kind: "CASE", target: selection, paymentId: id, expectedRevision: 3 },
  ],
  [
    "case.erase",
    { "input:expectedRevision": 3, "input:expectedFamilySequence": 4 },
    { kind: "CASE", target: selection, expectedRevision: 3, expectedFamilySequence: 4 },
  ],
  ["people.enable", { "input:actorId": id, "input:expectedRevision": 3 }, binding],
] as const)("interprets the owner binding plan for %s", (task, values, expected) => {
  expect(initialSubmissionBinding(task, { selection, values })).toEqual(expected);
});

it.each(["case.close", "people.enable", "reminder.update"] as const)(
  "never supplies creation zero for existing %s",
  (task) => {
    expect(() =>
      initialSubmissionBinding(task, {
        selection,
        values: {
          "input:actorId": id,
          "input:reminderId": id,
          "input:expectedRevision": 0,
          "input:expectedReminderRevision": 0,
        },
      }),
    ).toThrow("revision basis");
  },
);

it.each([
  { "input:scope": "INSTALLATION", "input:caseId": id },
  { "input:scope": "INSTALLATION", "field:caseReference": "CASE" },
  { "input:scope": "CASE", "input:caseId": id, "field:caseReference": "CASE" },
  { "input:scope": null },
  { "input:scope": false },
  { "input:scope": "INVALID" },
])("refuses conflicting raw scope without normalizing to installation %j", (values) => {
  expect(() => grantTarget(values)).toThrow("Invalid grant selection");
  expect(() =>
    createDraft({ subject: "PEOPLE", kind: "people.grant" }, id, binding, values, null, null),
  ).toThrow("Invalid grant selection");
  expect(() =>
    assembleTask("people.grant", { "input:role": "CASE_READER", ...values }, binding, id),
  ).toThrow("Invalid grant selection");
});

it("derives scope defaults from selector presence and retains malformed CASE text", () => {
  const task = operationTask("people.grant");
  expect(submissionDefaults(task, {})).toMatchObject({ "input:scope": "INSTALLATION" });
  expect(submissionDefaults(task, { "input:caseId": "malformed" })).toEqual({
    "input:scope": "CASE",
    "input:caseId": "malformed",
  });
  expect(grantTarget({ "input:scope": "CASE" })).toEqual({
    kind: "GRANT_TARGET",
    selection: { kind: "REFERENCE", text: "" },
  });
  expect(
    assembleTask(
      "people.grant",
      { "input:role": "CASE_READER", "input:caseId": "malformed" },
      binding,
      id,
    ).command.inputs,
  ).toEqual({ "input:role": "CASE_READER", "input:scope": "CASE", "input:caseId": "malformed" });
});

it("uses declared collection scope defaults without overriding an explicit raw case selector", () => {
  const result = assembleTask(
    "people.admit",
    {
      peopleGrants: {
        kind: "COLLECTION",
        rows: [{ key: "row", values: { "input:role": "CASE_READER", "input:caseId": id } }],
      },
    },
    { kind: "PEOPLE", expectedRevision: 0 },
    id,
  );
  expect(result.command.inputs).toMatchObject({
    "input:grants": [{ "input:role": "CASE_READER", "input:scope": "CASE", "input:caseId": id }],
  });
});

it.each([false, 0, "", null])(
  "preserves authored %j and omission while update hints stay dormant",
  (value) => {
    const task = operationTask("work.update");
    expect(
      mergeSubmissionInputs(task, { "field:handlingSummary": value }, ["field:handlingSummary"], {
        "field:responsibleActor": id,
      }),
    ).toEqual({ "field:handlingSummary": value });
    expect(
      mergeSubmissionInputs(task, {}, ["field:handlingSummary"], { "field:responsibleActor": id }),
    ).toEqual({});
  },
);
