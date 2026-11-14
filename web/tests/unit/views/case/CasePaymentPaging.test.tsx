import { operationAffordance } from "../../../support/affordances.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { requestText } from "../../../support/transport-response.fixtures";
import { current as source } from "../../../support/operation-form.fixtures";
import { workBasis } from "../../../support/inputs";
import type { CurrentCase } from "../../../../src/protocol/types";
import type { HumanReadDocument_case_payments, Region } from "@contracts/client";
import { CasePayments } from "../../../../src/views/case/CasePayments";
type Handle = { paymentId: string; canRemove: boolean };
const ids = [
  "10000000-0000-7000-8000-000000000101",
  "10000000-0000-7000-8000-000000000102",
  "10000000-0000-7000-8000-000000000103",
];
const older = "10000000-0000-7000-8000-000000000110";
const other = "10000000-0000-7000-8000-000000000111";
const last = "10000000-0000-7000-8000-000000000112";
const table = (handles: ReadonlyArray<Handle>, currency: string, firstAmount: number): Region => ({
  role: "PAYMENTS",
  titleId: "label.payments",
  content: {
    kind: "TABLE",
    columns: [
      {
        key: "paidAmount",
        labelId: "field.paidAmount.label",
        prominence: "PRIMARY",
        isMoney: true,
      },
    ],
    rows: handles.map((handle, index) => ({
      key: handle.paymentId,
      actions: handle.canRemove
        ? [
            operationAffordance("case.removePayment", {
              kind: "CASE",
              target: { kind: "ID", caseId: source.subject.caseId },
              expectedRevision: 7,
              paymentId: handle.paymentId,
            }),
          ]
        : [],
      values: [
        {
          kind: "MONEY",
          amount: String(firstAmount + index),
          currency,
          displayScale: 2,
        },
      ],
    })),
    sections: [
      {
        titleId: "label.paymentTotals",
        entries: [
          {
            key: "paymentCount",
            labelId: "label.payments",
            prominence: "PRIMARY",
            value: {
              kind: "COUNT",
              value: 55,
            },
          },
        ],
      },
    ],
  },
});
const preview = ids.map((paymentId) => ({
  paymentId,
  canRemove: true,
}));
const region = table(preview, "EUR", 1);
const current: CurrentCase = {
  ...source,
  screen: {
    regions: [region],
  },
  subject: { ...source.subject, revision: 7 },
  actions: source.actions.filter((action) =>
    new Set<string>(["case.removePayment"]).has(action.task),
  ),
};
const reply = (
  handles: ReadonlyArray<Handle>,
  cursor: string | null,
  revision = 7,
  disclosure: "CURRENT" | "ERASED" = "CURRENT",
) => {
  const document: HumanReadDocument_case_payments = {
    screen: {
      regions: disclosure === "ERASED" ? [] : [table(handles, "USD", 41)],
    },
    subject: { kind: "CASE", caseId: current.subject.caseId, revision, workRevision: 0 },
    actions: [],
    links: [],
    history: null,
    nextCursor: cursor,
    basis: {
      kind: "SNAPSHOT",
      observation: workBasis,
    },
    disclosure,
  };
  const value = {
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document,
    },
  };
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "Content-Type": "application/json",
    },
  });
};
afterEach(() => {
  vi.unstubAllGlobals();
});
it("expands a bounded preview using Core cursors and binds older-row removal to its supplied handle", async () => {
  const user = userEvent.setup();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      reply(
        [
          {
            paymentId: older,
            canRemove: true,
          },
          {
            paymentId: other,
            canRemove: false,
          },
        ],
        "core-payment-cursor",
      ),
    )
    .mockResolvedValueOnce(
      reply(
        [
          {
            paymentId: last,
            canRemove: true,
          },
        ],
        null,
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const onAct = vi.fn();
  const onChanged = vi.fn();
  render(<CasePayments region={region} current={current} onAct={onAct} onChanged={onChanged} />);
  expect(fetch).not.toHaveBeenCalled();
  expect(screen.getByText("3.00 EUR")).toBeVisible();
  await user.click(
    screen.getByRole("button", {
      name: "Show all payments",
    }),
  );
  await screen.findByText("41.00 USD");
  expect(screen.queryByText("3.00 EUR")).not.toBeInTheDocument();
  expect(
    screen.getAllByRole("button", {
      name: "Remove payment entry",
    }),
  ).toHaveLength(1);
  expect(JSON.parse(requestText(fetch.mock.calls[0]?.[1]?.body))).toEqual({
    target: {
      kind: "ID",
      caseId: current.subject.caseId,
    },
  });
  expect(fetch.mock.calls[0]?.[1]?.headers).toMatchObject({
    "ClaimCore-Representation": "HUMAN",
  });
  await user.click(
    screen.getByRole("button", {
      name: "More payments",
    }),
  );
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
    target: {
      kind: "ID",
      caseId: current.subject.caseId,
    },
    cursor: "core-payment-cursor",
  });
  await user.click(
    screen.getAllByRole("button", {
      name: "Remove payment entry",
    })[0]!,
  );
  expect(onAct).toHaveBeenCalledWith("case.removePayment", older);
  expect(onChanged).not.toHaveBeenCalled();
});
it("masks earlier payment rows on an erased continuation and refreshes the case disclosure", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        reply(
          [
            {
              paymentId: older,
              canRemove: true,
            },
          ],
          "issued",
        ),
      )
      .mockResolvedValueOnce(reply([], null, 8, "ERASED")),
  );
  const onChanged = vi.fn();
  render(<CasePayments region={region} current={current} onAct={vi.fn()} onChanged={onChanged} />);
  await user.click(
    screen.getByRole("button", {
      name: "Show all payments",
    }),
  );
  await screen.findByText("41.00 USD");
  await user.click(
    screen.getByRole("button", {
      name: "More payments",
    }),
  );
  await waitFor(() => {
    expect(onChanged).toHaveBeenCalled();
  });
  expect(screen.queryByText("41.00 USD")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", {
      name: "Remove payment entry",
    }),
  ).not.toBeInTheDocument();
});
it("refuses to expose payment rows from a newer case version until its current document is refreshed", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(
      reply(
        [
          {
            paymentId: older,
            canRemove: true,
          },
        ],
        null,
        8,
      ),
    ),
  );
  const onChanged = vi.fn();
  render(<CasePayments region={region} current={current} onAct={vi.fn()} onChanged={onChanged} />);
  await user.click(
    screen.getByRole("button", {
      name: "Show all payments",
    }),
  );
  await waitFor(() => {
    expect(onChanged).toHaveBeenCalled();
  });
  expect(screen.queryByText("41.00 USD")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", {
      name: "Remove payment entry",
    }),
  ).not.toBeInTheDocument();
});
