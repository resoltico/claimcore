import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { workBasis } from "../../../support/inputs";
import { current as bindingDocument } from "../../../support/operation-form.fixtures";
import type { CurrentCase } from "../../../../src/protocol/types";
import { CasePayments } from "../../../../src/views/case/CasePayments";
import { MoneyMeter } from "../../../../src/ui/regions/MoneyMeter";
import type { Region } from "@contracts/client";
const first = "10000000-0000-7000-8000-000000000003";
const second = "10000000-0000-7000-8000-000000000004";
const current: CurrentCase = {
  ...bindingDocument,
  context: {
    ...bindingDocument.context,
    tasks: ["CASE:REMOVE_PAYMENT"],
    correction: null,
    payments: [
      { paymentId: first, canRemove: true },
      { paymentId: second, canRemove: true },
    ],
  },
};
const supplied: Region = {
  role: "PAYMENTS",
  titleId: "label.paymentDecisionAndPayments",
  content: {
    kind: "TABLE",
    columns: [
      {
        key: "paymentDate",
        labelId: "field.paymentDate.label",
        prominence: "PRIMARY",
        isMoney: false,
      },
      {
        key: "paidAmount",
        labelId: "field.paidAmount.label",
        prominence: "PRIMARY",
        isMoney: true,
      },
    ],
    rows: [
      {
        key: second,
        values: [
          { kind: "DATE", value: "2026-09-03" },
          { kind: "MONEY", amount: "77", currency: "USD", displayScale: 2 },
        ],
      },
      {
        key: first,
        values: [
          { kind: "DATE", value: "2026-09-02" },
          { kind: "MONEY", amount: "23", currency: "USD", displayScale: 2 },
        ],
      },
    ],
    sections: [],
  },
};
it("renders supplied payment facts and binds reordered row removal by entry identity", async () => {
  const onAct = vi.fn();
  render(<CasePayments region={supplied} current={current} onAct={onAct} />);
  expect(screen.getByText("77.00 USD")).toBeVisible();
  expect(screen.queryByText("30.00 EUR")).toBeNull();
  expect(screen.queryByRole("button", { name: "Change decision" })).toBeNull();
  await userEvent
    .setup()
    .click(screen.getAllByRole("button", { name: "Remove payment entry" })[0]!);
  expect(onAct).toHaveBeenCalledExactlyOnceWith("REMOVE_PAYMENT", second);
});
it("offers no removal without the supplied offered-act flag", () => {
  render(
    <CasePayments
      region={supplied}
      current={{
        ...current,
        context: {
          ...current.context,
          tasks: [],
          payments: current.context.payments.map((payment) => ({ ...payment, canRemove: false })),
        },
      }}
      onAct={vi.fn()}
    />,
  );
  expect(screen.queryByRole("button", { name: "Remove payment entry" })).toBeNull();
});
it("represents a completed zero liability with a native range and exact money text", () => {
  render(
    <MoneyMeter
      timeZone={workBasis.timeZone}
      label="Paid"
      meter={{
        current: { amount: "0", currency: "EUR", displayScale: 2 },
        target: { amount: "0", currency: "EUR", displayScale: 2 },
        complete: true,
        description: {
          messageId: "label.paymentProgress",
          arguments: [
            {
              key: "paid",
              value: { kind: "MONEY", amount: "0", currency: "EUR", displayScale: 2 },
            },
            {
              key: "payable",
              value: { kind: "MONEY", amount: "0", currency: "EUR", displayScale: 2 },
            },
          ],
        },
      }}
    />,
  );
  const meter = screen.getByRole("meter", { name: "Paid" });
  expect(meter).toHaveAttribute("value", "1");
  expect(meter).toHaveAttribute("max", "1");
  expect(document.body).toHaveTextContent("Paid 0.00 EUR of 0.00 EUR.");
});
