import { expect, it } from "vitest";
import { replyCorrelates, receiptCorrelates } from "../../../src/protocol/replyCorrelation";

const operationId = "30000000-0000-4000-8000-000000000001";
const caseId = "30000000-0000-4000-8000-000000000002";
const reminderId = "30000000-0000-4000-8000-000000000003";
const otherId = "30000000-0000-4000-8000-000000000004";
const command = {
  kind: "REMINDER",
  operationId,
  target: { kind: "ID", caseId },
  act: {
    kind: "COMPLETE_AND_PLAN_NEXT",
    reminderId,
    expectedRevision: 3,
    action: "Next action",
    dueDate: "2026-11-10",
  },
};
const completed = {
  reminderId,
  revision: 4,
  snapshot: {
    kind: "DISCLOSED",
    view: {
      reminderId,
      caseId,
      revision: 4,
      action: "Old action",
      dueDate: "2026-11-01",
      state: "COMPLETED",
    },
  },
};
const next = {
  reminderId: operationId,
  revision: 1,
  snapshot: {
    kind: "DISCLOSED",
    view: {
      reminderId: operationId,
      caseId,
      revision: 1,
      action: "Next action",
      dueDate: "2026-11-10",
      state: "PENDING",
    },
  },
};
const receipt = {
  operationId,
  subject: {
    kind: "REMINDER",
    caseId,
    caseReference: "REF-1",
    act: "COMPLETE_AND_PLAN_NEXT",
    effects: { kind: "COMPLETED_AND_PLANNED", completed, next },
  },
};
it("correlates a complete fixed pair with its original family parent action and entity revision", () => {
  expect(receiptCorrelates(command, receipt)).toBe(true);
  expect(
    replyCorrelates("operation.execute", command, {
      operationId,
      outcome: { kind: "ACCEPTED", data: receipt },
    }),
  ).toBe(true);
  expect(
    receiptCorrelates(
      { ...command, target: { kind: "REFERENCE", caseReference: "ref-1" } },
      receipt,
    ),
  ).toBe(true);
  expect(receiptCorrelates({ ...command, operationId: otherId }, receipt)).toBe(false);
  expect(receiptCorrelates({ ...command, target: { kind: "ID", caseId: otherId } }, receipt)).toBe(
    false,
  );
  expect(
    receiptCorrelates(
      { ...command, target: { kind: "REFERENCE", caseReference: "OTHER" } },
      receipt,
    ),
  ).toBe(false);
  expect(
    receiptCorrelates({ ...command, act: { ...command.act, kind: "COMPLETE" } }, receipt),
  ).toBe(false);
  expect(
    receiptCorrelates({ ...command, act: { ...command.act, expectedRevision: 2 } }, receipt),
  ).toBe(false);
});
it("rejects plausible compound effects with wrong causal identity state parent revision or arity", () => {
  const variants = [
    { ...receipt.subject.effects, next: { ...next, reminderId: otherId } },
    { ...receipt.subject.effects, next: { ...next, revision: 2 } },
    { ...receipt.subject.effects, completed: next, next: completed },
    {
      ...receipt.subject.effects,
      completed: {
        ...completed,
        snapshot: { kind: "DISCLOSED", view: { ...completed.snapshot.view, caseId: otherId } },
      },
    },
    {
      ...receipt.subject.effects,
      completed: {
        ...completed,
        snapshot: { kind: "DISCLOSED", view: { ...completed.snapshot.view, state: "PENDING" } },
      },
    },
    {
      ...receipt.subject.effects,
      next: {
        ...next,
        snapshot: { kind: "DISCLOSED", view: { ...next.snapshot.view, state: "COMPLETED" } },
      },
    },
    { kind: "SINGLE", reminder: completed },
  ];
  for (const effects of variants) {
    expect(
      receiptCorrelates(command, { ...receipt, subject: { ...receipt.subject, effects } }),
    ).toBe(false);
  }
  expect(receiptCorrelates(command, receipt)).toBe(true);
});
it("checks the advisory compound pair independently of mere schema parsing", () => {
  const subject = {
    kind: "REMINDER",
    caseId,
    caseReference: "REF-1",
    before: { ...completed.snapshot.view, revision: 3, state: "PENDING" },
    effects: {
      kind: "COMPLETED_AND_PLANNED",
      completed: completed.snapshot.view,
      next: next.snapshot.view,
    },
  };
  const answer = { operationId, outcome: { kind: "REVIEWED", data: { review: { subject } } } };
  expect(replyCorrelates("operation.preview", command, answer)).toBe(true);
  expect(
    replyCorrelates("operation.preview", command, {
      ...answer,
      outcome: {
        kind: "REVIEWED",
        data: {
          review: {
            subject: {
              ...subject,
              effects: { ...subject.effects, next: { ...next.snapshot.view, reminderId: otherId } },
            },
          },
        },
      },
    }),
  ).toBe(false);
  expect(
    replyCorrelates(
      "operation.preview",
      { ...command, act: { ...command.act, expectedRevision: 2 } },
      answer,
    ),
  ).toBe(false);
});
it("keeps work revision separate and checks disclosed parent consistency", () => {
  const input = {
    kind: "WORK",
    operationId,
    target: { kind: "ID", caseId },
    expectedRevision: 1,
    act: { kind: "UPDATE_WORK", values: { handlingSummary: "summary" } },
  };
  const result = {
    operationId,
    subject: {
      kind: "WORK",
      caseId,
      caseReference: "REF-1",
      revision: 2,
      act: "UPDATE_WORK",
      snapshot: {
        kind: "DISCLOSED",
        view: { caseId, revision: 2, work: { responsibleActor: null, handlingSummary: "summary" } },
      },
    },
  };
  expect(receiptCorrelates(input, result)).toBe(true);
  expect(receiptCorrelates({ ...input, expectedRevision: 9 }, result)).toBe(false);
  expect(
    receiptCorrelates(input, { ...result, subject: { ...result.subject, kind: "CASE" } }),
  ).toBe(false);
  expect(
    receiptCorrelates(input, {
      ...result,
      subject: {
        ...result.subject,
        snapshot: { kind: "DISCLOSED", view: { caseId: otherId, revision: 2 } },
      },
    }),
  ).toBe(false);
});
it("requires create identity to equal its operation during identity-only status too", () => {
  const create = {
    ...command,
    act: { kind: "CREATE", action: "Next action", dueDate: "2026-11-10" },
  };
  const accepted = {
    ...receipt,
    subject: { ...receipt.subject, act: "CREATE", effects: { kind: "SINGLE", reminder: next } },
  };
  expect(receiptCorrelates(create, accepted)).toBe(true);
  expect(receiptCorrelates({ operationId }, accepted)).toBe(true);
  expect(
    receiptCorrelates(
      { operationId },
      {
        ...accepted,
        subject: {
          ...accepted.subject,
          effects: { kind: "SINGLE", reminder: { ...next, reminderId: otherId } },
        },
      },
    ),
  ).toBe(false);
});
