import { expect, it } from "vitest";
import { isRequest, isResponse } from "@contracts/client";
const caseId = "019aa6a0-0000-7000-8000-000000000001";
const paymentId = "019aa6a0-0000-7000-8000-000000000002";
const basis = {
  observedAt: "2026-11-09T12:00:00.000000Z",
  businessDate: "2026-11-09",
  timeZone: "Etc/UTC",
};
const context = {
  kind: "PAYMENT_PAGE",
  caseId,
  revision: 3,
  nextCursor: null,
  payments: [{ paymentId, canRemove: true }],
};
const paymentDocument = {
  screen: {
    regions: [
      {
        role: "PAYMENTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "saved", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [{ key: paymentId, values: [{ kind: "TEXT", value: "Core payment caption Ω" }] }],
        },
      },
    ],
  },
  context,
  basis: { kind: "SNAPSHOT", observation: basis },
  disclosure: "CURRENT",
};
const human = (documentValue: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: documentValue },
});
it("keeps payment read bounds distinct from the total number of entries", () => {
  const input = { target: { kind: "ID", caseId } };
  expect(isRequest("case.payments", input)).toBe(true);
  expect(isRequest("case.payments", { ...input, limit: 50 })).toBe(true);
  expect(isRequest("case.payments", { ...input, limit: 51 })).toBe(false);
});
it("admits authored saved captions and rejects orphaned duplicate or erased payment handles", () => {
  expect(isResponse("case.payments", human(paymentDocument))).toBe(true);
  for (const altered of [
    { ...context, payments: [] },
    { ...context, payments: [...context.payments, ...context.payments] },
    {
      ...context,
      payments: [{ paymentId: "019aa6a0-0000-7000-8000-000000000009", canRemove: true }],
    },
  ]) {
    expect(isResponse("case.payments", human({ ...paymentDocument, context: altered }))).toBe(
      false,
    );
  }
  expect(isResponse("case.payments", human({ ...paymentDocument, disclosure: "ERASED" }))).toBe(
    false,
  );
});
it("structural DATA admission does not implement the core financial equations", () => {
  const data = {
    representation: "DATA",
    outcome: {
      kind: "SUCCEEDED",
      data: {
        basis,
        caseId,
        caseReference: "FIN-1",
        revision: 3,
        state: {
          kind: "ACTIVE",
          finance: {
            decision: {
              paymentDecisionDate: "2026-01-10",
              payableAmount: "10",
              payableCurrency: "EUR",
            },
            paidAmount: "99",
            paymentCount: 1,
            firstPaymentDate: "2026-01-11",
            lastPaymentDate: "2026-01-11",
          },
        },
        items: [
          {
            entry: { paymentId, paymentDate: "2026-01-11", paidAmount: "1" },
            position: { sequence: 3, effectIndex: 0 },
            canRemove: true,
          },
        ],
        nextCursor: null,
      },
    },
  };
  expect(isResponse("case.payments", data)).toBe(true);
  expect(
    isResponse("case.payments", {
      ...data,
      outcome: { ...data.outcome, data: { ...data.outcome.data, unexpected: true } },
    }),
  ).toBe(false);
});
