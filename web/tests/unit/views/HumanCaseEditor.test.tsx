import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { OperationEditor } from "../../../src/views/OperationEditor";
import { productMetadata } from "../../../src/protocol/productMetadata";
import {
  isRequest,
  isResponse,
  type HumanReview,
  type HumanReceiptDocument,
} from "@contracts/client";
import type { CurrentCase } from "../../../src/protocol/types";

const caseId = "00000000-0000-4000-8000-000000000001";
const paymentId = "019aa6a0-0000-7000-8000-000000000002";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const current: CurrentCase = {
  screen: {
    regions: [
      {
        role: "PAYMENTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [{ key: paymentId, values: [{ kind: "TEXT", value: "Core saved payment Ω" }] }],
        },
      },
    ],
  },
  context: {
    kind: "CASE",
    caseId,
    revision: 7,
    workRevision: 4,
    history: "NONE",
    tasks: ["CASE:REMOVE_PAYMENT"],
    editableInputs: [],
    correction: null,
    seeds: { registration: null, decision: null, work: null, paymentCurrency: null },
    notes: [],
    payments: [{ paymentId, canRemove: true }],
    nextActions: [],
  },
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: { changes: [], unchanged: [] },
  document: {
    screen: { regions: [] },
    context: {
      kind: "REVIEW",
      task: "CASE:REMOVE_PAYMENT",
      target: { kind: "CASE", caseId, beforeRevision: 7 },
      inputs: [
        "input:operationId",
        "field:caseReference",
        "input:expectedRevision",
        "input:paymentId",
      ],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "ORIGINAL-CASE" },
    },
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "receipt",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core full accepted case Ψ" },
            },
          ],
        },
      },
    ],
  },
  context: { kind: "RECEIPT", target: { kind: "CASE", caseId, revision: 8 }, refreshCase: caseId },
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
it("previews the typed selected payment and preserves selector spelling through consent and the full Core receipt", async () => {
  const bodies: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation(async (url, options) => {
      const body = String(options?.body);
      const command: unknown = JSON.parse(body);
      if (!isRequest("operation.execute", command)) {
        throw new Error("Expected typed operation");
      }
      bodies.push(body);
      return new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: command.operationId,
          outcome: String(url).endsWith("preview")
            ? { kind: "REVIEWED", review }
            : { kind: "ACCEPTED", document: receipt },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      );
    }),
  );
  const committed = vi.fn();
  render(
    <OperationEditor
      definition={productMetadata}
      current={current}
      initialAct="REMOVE_PAYMENT"
      binding={{
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "ORIGINAL-CASE" },
        revision: 7,
        paymentId,
      }}
      autoReview
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  await waitFor(() =>
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull(),
  );
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await screen.findByText("Core full accepted case Ψ");
  expect(committed).toHaveBeenCalledExactlyOnceWith(receipt, JSON.parse(bodies[0]!).operationId);
  expect(bodies).toHaveLength(2);
  expect(bodies[1]).toBe(bodies[0]);
  expect(JSON.parse(bodies[0]!)).toMatchObject({
    kind: "CASE",
    target: { kind: "REFERENCE", caseReference: "ORIGINAL-CASE" },
    expectedRevision: 7,
    act: { kind: "REMOVE_PAYMENT", paymentId },
  });
});
it("takes the payment currency default only from an offered Core input seed", () => {
  const payment: CurrentCase = {
    ...current,
    context: {
      ...current.context,
      tasks: ["CASE:RECORD_PAYMENT"],
      payments: [{ paymentId, canRemove: false }],
      seeds: { ...current.context.seeds, paymentCurrency: "EUR" },
    },
  };
  expect(
    isResponse("case.get", {
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: payment },
    }),
  ).toBe(true);
  render(
    <OperationEditor
      definition={productMetadata}
      current={payment}
      initialAct="RECORD_PAYMENT"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  expect(screen.getByRole("combobox", { name: /currency/i })).toHaveValue("EUR");
  expect(
    screen.queryByRole("textbox", { name: /expected.*revision|operation.*id/i }),
  ).not.toBeInTheDocument();
  expect(
    isResponse("case.get", {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: { ...payment, context: { ...payment.context, tasks: [] } },
      },
    }),
  ).toBe(false);
});
