import { operationAffordance } from "../../support/affordances.fixtures";
import type { RequestByEndpoint, HumanReview, HumanReceiptDocument } from "@contracts/client";
import {
  operationRequest,
  requestText,
  requestAddress,
} from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { OperationEditor } from "../../../src/views/OperationEditor";
import { productMetadata } from "../../../src/protocol/productMetadata";
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
            {
              key: "caption",
              labelId: "label.details",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: paymentId,
              values: [
                {
                  kind: "TEXT",
                  value: "Core saved payment Ω",
                },
              ],
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "CASE", caseId, revision: 7, workRevision: 4 },
  actions: [
    operationAffordance(
      "case.removePayment",
      { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7, paymentId },
      {},
      [],
      {},
    ),
  ],
  links: [],
  history: "NONE",
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: {
    changes: [],
    unchanged: [],
  },
  document: {
    screen: {
      regions: [],
    },
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "ORIGINAL-CASE",
      },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "case.removePayment",
        { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7, paymentId },
        {},
        [],
      ),
    ],
    links: [],
    history: null,
    nextCursor: null,
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
              value: {
                kind: "TEXT",
                value: "Core full accepted case Ψ",
              },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
it("previews the typed selected payment and preserves selector spelling through consent and the full Core receipt", async () => {
  const bodies: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        const body = requestText(options?.body);
        const command: unknown = JSON.parse(body);
        bodies.push(body);
        const operationExecuteRequest = command as RequestByEndpoint["operation.execute"];
        return new Response(
          JSON.stringify({
            representation: "HUMAN",
            operationId: operationExecuteRequest.operationId,
            outcome: requestAddress(url).endsWith("preview")
              ? {
                  kind: "REVIEWED",
                  review,
                }
              : {
                  kind: "ACCEPTED",
                  document: receipt,
                },
          }),
          {
            status: 200,
            headers: {
              "Content-Type": "application/json",
            },
          },
        );
      }),
    ),
  );
  const committed = vi.fn();
  render(
    <OperationEditor
      definition={productMetadata}
      current={current}
      initialAct="case.removePayment"
      binding={{
        kind: "CASE",
        target: {
          kind: "REFERENCE",
          caseReference: "ORIGINAL-CASE",
        },
        expectedRevision: 7,
        paymentId,
      }}
      autoReview
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  await waitFor(() => {
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull();
  });
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await screen.findByText("Core full accepted case Ψ");
  expect(committed).toHaveBeenCalledExactlyOnceWith(
    { document: receipt, caseId },
    operationRequest(bodies[0]).operationId,
  );
  expect(bodies).toHaveLength(2);
  expect(bodies[1]).toBe(bodies[0]);
  expect(JSON.parse(bodies[0]!)).toMatchObject({
    task: "case.removePayment",
    binding: {
      kind: "CASE",
      target: {
        kind: "REFERENCE",
        caseReference: "ORIGINAL-CASE",
      },
      expectedRevision: 7,
      paymentId,
    },
    inputs: {},
  });
});
it("takes the payment currency default only from an offered Core input seed", () => {
  const payment: CurrentCase = {
    ...current,
    actions: [
      operationAffordance(
        "case.recordPayment",
        { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7 },
        { "field:paidCurrency": "EUR" },
      ),
    ],
  };
  render(
    <OperationEditor
      definition={productMetadata}
      current={payment}
      initialAct="case.recordPayment"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  expect(
    screen.getByRole("combobox", {
      name: /currency/iu,
    }),
  ).toHaveValue("EUR");
  expect(
    screen.queryByRole("textbox", {
      name: /expected.*revision|operation.*id/iu,
    }),
  ).not.toBeInTheDocument();
});
