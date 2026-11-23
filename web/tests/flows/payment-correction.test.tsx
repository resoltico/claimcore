import { selectRowAction } from "../support/workspace-actions";
import { useState } from "react";
import type {
  Affordance,
  HumanDocument,
  HumanReview,
  Region,
  RequestByEndpoint,
} from "@contracts/client";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { operationAffordance, requireOperationAffordance } from "../support/affordances.fixtures";
import { current as original } from "../support/operation-form.fixtures";
import {
  endpointResponse,
  requestText,
  requestAddress,
} from "../support/transport-response.fixtures";
import { input } from "../support/catalog-transport.fixtures";
import { reviewConfirm } from "../support/review-controls";
import { PaymentRows } from "../../src/views/case/PaymentRows";
import { OperationEditor } from "../../src/views/OperationEditor";
import { productMetadata } from "../../src/protocol/productMetadata";
const oldId = "10000000-0000-7000-8000-000000000003";
const binding = {
  kind: "CASE" as const,
  target: { kind: "ID" as const, caseId: original.subject.caseId },
  expectedRevision: 4,
  paymentId: oldId,
};
const action = operationAffordance("case.correctPayment", binding, { "field:paidCurrency": "EUR" });
const current = { ...original, actions: [action] };
type Table = Extract<Region["content"], { kind: "TABLE" }>;
type Command = RequestByEndpoint["operation.execute"];
const paid = (amount: string) => ({
  kind: "MONEY" as const,
  amount,
  currency: "EUR",
  displayScale: 2,
});
const source: Table = {
  kind: "TABLE",
  columns: [
    { key: "amount", labelId: "field.paidAmount.label", prominence: "PRIMARY", isMoney: true },
  ],
  rows: [
    { key: "other", values: [paid("10")], actions: [] },
    { key: oldId, values: [paid("25")], actions: [action] },
  ],
};
const review = (): HumanReview => ({
  document: {
    subject: {
      kind: "REVIEW",
      confirmation: { kind: "CASE_REFERENCE", reference: "ROW-CORRECTION" },
      consequence: "ROUTINE",
    },
    screen: { regions: [] },
    actions: [operationAffordance("case.correctPayment", binding, {}, [])],
    links: [],
    history: null,
    nextCursor: null,
    basis: current.basis,
    disclosure: "CURRENT",
  },
  presentation: {
    changes: [
      {
        key: "replacement",
        labelId: "field.paidAmount.label",
        before: paid("25"),
        after: paid("40"),
      },
    ],
    unchanged: [],
  },
});
const receipt = (operationId: string): HumanDocument => ({
  subject: { kind: "RECEIPT", operationId },
  screen: {
    regions: [
      {
        role: "PAYMENTS",
        titleId: "label.payments",
        content: {
          kind: "TABLE",
          columns: [
            { key: "direction", labelId: "label.action", prominence: "PRIMARY", isMoney: false },
            {
              key: "paymentId",
              labelId: "field.paymentId.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: oldId,
              values: [
                { kind: "MESSAGE", messageId: "task.case.removePayment.done" },
                { kind: "IDENTIFIER", value: oldId },
              ],
            },
            {
              key: operationId,
              values: [
                { kind: "MESSAGE", messageId: "task.case.recordPayment.done" },
                { kind: "IDENTIFIER", value: operationId },
              ],
            },
          ],
        },
      },
    ],
  },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: binding.target } },
    },
  ],
  history: null,
  nextCursor: null,
  basis: current.basis,
  disclosure: "CURRENT",
});
const SelectedCorrection = ({
  committed,
}: {
  committed: (receipt: unknown, operationId: string) => void;
}) => {
  const [selected, setSelected] = useState<Affordance | null>(null);
  return selected === null ? (
    <PaymentRows content={source} zone="UTC" onAction={setSelected} />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={current}
      initialAct="case.correctPayment"
      affordance={requireOperationAffordance(selected)}
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />
  );
};
afterEach(() => vi.unstubAllGlobals());
it("selects the exact payment row, confirms frozen correction bytes and renders both supplied receipt directions", async () => {
  const calls: { path: string; bytes: string; command: Command }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((path, options) => {
      const bytes = requestText(options?.body);
      const command = JSON.parse(bytes) as Command;
      calls.push({ path: requestAddress(path), bytes, command });
      if (requestAddress(path).endsWith("operation.preview")) {
        return Promise.resolve(
          endpointResponse("operation.preview", {
            representation: "HUMAN",
            outcome: { kind: "REVIEWED", review: review() },
          }),
        );
      }
      if (requestAddress(path).endsWith("operation.execute")) {
        return Promise.resolve(
          endpointResponse("operation.execute", {
            representation: "HUMAN",
            outcome: { kind: "ACCEPTED", document: receipt(command.operationId) },
          }),
        );
      }
      throw new Error("Only explicit preview and confirmed execute are expected");
    }),
  );
  const committed = vi.fn();
  render(<SelectedCorrection committed={committed} />);
  await selectRowAction(
    userEvent.setup(),
    screen.getByRole("row", { name: /25.00 EUR/u }),
    "Correct payment entry",
  );
  expect(screen.queryByRole("textbox", { name: "Payment ID" })).toBeNull();
  input("Payment date", "2026-09-05");
  input("Paid amount", "40");
  await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(reviewConfirm()).toBeEnabled();
  });
  expect(calls).toHaveLength(1);
  expect(calls[0]?.command).toMatchObject({
    task: "case.correctPayment",
    binding,
    inputs: {
      "field:paymentDate": "2026-09-05",
      "field:paidAmount": "40",
      "field:paidCurrency": "EUR",
    },
  });
  await userEvent.click(reviewConfirm());
  await waitFor(() => {
    expect(committed).toHaveBeenCalledOnce();
  });
  expect(calls).toHaveLength(2);
  expect(calls[1]?.path).toBe("/api/operation.execute");
  expect(calls[1]?.bytes).toBe(calls[0]?.bytes);
  expect(screen.getByText("Payment entry removed.")).toBeVisible();
  expect(screen.getByText("Payment recorded.")).toBeVisible();
  expect(screen.getByText(oldId)).toBeVisible();
  expect(screen.getByText(calls[0]!.command.operationId)).toBeVisible();
});
