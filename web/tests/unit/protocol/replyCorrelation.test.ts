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
    parent: { caseId, caseReference: "REF-1", revision: 99 },
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
            presentation: { changes: [], unchanged: [] },
            subject: { ...subject, parent: { ...subject.parent, caseId: otherId } },
          },
        },
      },
    }),
  ).toBe(false);

  expect(
    replyCorrelates("operation.preview", command, {
      ...answer,
      outcome: {
        kind: "REVIEWED",
        data: {
          review: {
            presentation: { changes: [], unchanged: [] },
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

it("correlates case preview identity and exact next version without interpreting its supplied facet", () => {
  const reviewed = (subject: unknown) => ({
    operationId,
    outcome: {
      kind: "REVIEWED",
      data: { review: { subject, presentation: { changes: [], unchanged: [] } } },
    },
  });
  const open = {
    kind: "CASE",
    operationId,
    target: { kind: "REFERENCE", caseReference: "ref-1" },
    expectedRevision: 0,
    act: { kind: "OPEN" },
  };
  const opened = {
    kind: "CASE",
    before: null,
    after: { caseId: operationId, caseReference: "REF-1", revision: 1 },
  };
  expect(replyCorrelates("operation.preview", open, reviewed(opened))).toBe(true);
  expect(
    replyCorrelates(
      "operation.preview",
      open,
      reviewed({ ...opened, after: { ...opened.after, revision: 999 } }),
    ),
  ).toBe(false);
  expect(
    replyCorrelates(
      "operation.preview",
      open,
      reviewed({ ...opened, after: { ...opened.after, caseId } }),
    ),
  ).toBe(false);
  expect(
    replyCorrelates("operation.preview", { ...open, expectedRevision: 1 }, reviewed(opened)),
  ).toBe(false);
  const close = {
    ...open,
    target: { kind: "ID", caseId },
    expectedRevision: 4,
    act: { kind: "CLOSE" },
  };
  const before = { caseId, caseReference: "REF-1", revision: 4 };
  const closed = { kind: "CASE", before, after: { ...before, revision: 5 } };
  expect(replyCorrelates("operation.preview", close, reviewed(closed))).toBe(true);
  expect(
    replyCorrelates(
      "operation.preview",
      close,
      reviewed({ ...closed, after: { ...closed.after, caseReference: "ref-1" } }),
    ),
  ).toBe(true);
  for (const subject of [
    { ...closed, before: null },
    { ...closed, before: { ...before, revision: 3 } },
    { ...closed, before: { ...before, caseId: otherId } },
    { ...closed, after: { ...closed.after, revision: 999 } },
    { ...closed, after: { ...closed.after, caseReference: "OTHER" } },
  ]) {
    expect(replyCorrelates("operation.preview", close, reviewed(subject))).toBe(false);
  }
});

it("correlates exact authority principals and ordered effect kinds without deriving profile or access policy", () => {
  const target = { kind: "HUMAN", issuer: "https://issuer.example/", subject: "Immutable-A" };
  const grant = {
    kind: "AUTHORITY",
    operationId,
    target,
    act: { kind: "GRANT", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
  };
  const subject = {
    kind: "AUTHORITY",
    target: { actor: { actorId: caseId }, principal: target },
    effects: [{ kind: "GRANT" }],
  };
  const reviewed = (value: unknown) => ({
    operationId,
    outcome: {
      kind: "REVIEWED",
      data: { review: { subject: value, presentation: { changes: [], unchanged: [] } } },
    },
  });
  expect(replyCorrelates("operation.preview", grant, reviewed(subject))).toBe(true);
  for (const principal of [
    { ...target, kind: "SERVICE" },
    { ...target, issuer: "https://other.example/" },
    { ...target, subject: "immutable-a" },
  ]) {
    expect(
      replyCorrelates(
        "operation.preview",
        grant,
        reviewed({ ...subject, target: { ...subject.target, principal } }),
      ),
    ).toBe(false);
  }
  expect(replyCorrelates("operation.preview", grant, reviewed({ ...subject, target: null }))).toBe(
    false,
  );
  expect(
    replyCorrelates(
      "operation.preview",
      grant,
      reviewed({ ...subject, effects: [{ kind: "DISABLE" }] }),
    ),
  ).toBe(false);
  const admit = {
    ...grant,
    act: { kind: "ADMIT", profile: { displayName: "Authored" }, grants: [grant.act, grant.act] },
  };
  const effects = [
    { kind: "REGISTER", principal: target },
    { kind: "SET_PROFILE" },
    { kind: "GRANT" },
    { kind: "GRANT" },
  ];
  const admitted = { kind: "AUTHORITY", target: null, effects };
  expect(replyCorrelates("operation.preview", admit, reviewed(admitted))).toBe(true);
  for (const changed of [
    effects.slice(0, 3),
    [effects[1], effects[0], effects[2], effects[3]],
    [{ kind: "REGISTER", principal: { ...target, subject: "OTHER" } }, ...effects.slice(1)],
  ]) {
    expect(
      replyCorrelates("operation.preview", admit, reviewed({ ...admitted, effects: changed })),
    ).toBe(false);
  }
  const register = { ...grant, act: { kind: "REGISTER", profile: { displayName: "Authored" } } };
  expect(
    replyCorrelates(
      "operation.preview",
      register,
      reviewed({ ...admitted, effects: effects.slice(0, 2) }),
    ),
  ).toBe(true);
  expect(replyCorrelates("operation.preview", register, reviewed(admitted))).toBe(false);
});

it("checks deterministic creation identities even for passive receipt observations", () => {
  const opened = { operationId, subject: { kind: "CASE", act: "OPEN", caseId: operationId } };
  expect(receiptCorrelates({ operationId }, opened)).toBe(true);
  expect(
    receiptCorrelates(
      { operationId },
      { ...opened, subject: { ...opened.subject, caseId: otherId } },
    ),
  ).toBe(false);
  for (const act of ["REGISTER", "ADMIT", "PROVISION_OWNER"]) {
    for (const targetActor of [
      { kind: "DISCLOSED", actor: { actorId: operationId } },
      { kind: "WITHHELD", actorId: operationId },
    ]) {
      const created = { operationId, subject: { kind: "AUTHORITY", act, targetActor } };
      expect(receiptCorrelates({ operationId }, created)).toBe(true);
      const wrong =
        targetActor.kind === "DISCLOSED"
          ? { ...targetActor, actor: { actorId: otherId } }
          : { ...targetActor, actorId: otherId };
      expect(
        receiptCorrelates(
          { operationId },
          { ...created, subject: { ...created.subject, targetActor: wrong } },
        ),
      ).toBe(false);
    }
  }
  const existing = {
    operationId,
    subject: {
      kind: "AUTHORITY",
      act: "DISABLE",
      targetActor: { kind: "WITHHELD", actorId: otherId },
    },
  };
  expect(receiptCorrelates({ operationId }, existing)).toBe(true);
});
