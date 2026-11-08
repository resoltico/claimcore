import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { currentCaseInput, registration, workBasis } from "../../../support/inputs";
import { caseAdvice } from "../../../support/case-advice.fixtures";
import { CasePayments } from "../../../../src/views/case/CasePayments";
import { MoneyMeter } from "../../../../src/ui/regions/MoneyMeter";
import type { Region } from "@contracts/client";
const first = "10000000-0000-7000-8000-000000000003";
const second = "10000000-0000-7000-8000-000000000004";
const current = currentCaseInput({
  caseReference: "PAY-UI",
  revision: 4,
  advice: { ...caseAdvice(["REMOVE_PAYMENT"]), history: "FULL" },
  state: {
    kind: "ACTIVE",
    fields: {
      caseReference: "PAY-UI",
      status: "OPENED",
      registration: registration(),
      decision: { paymentDecisionDate: "2026-09-01", payableAmount: "100", payableCurrency: "EUR" },
      payments: [
        { paymentId: first, paymentDate: "2026-09-02", paidAmount: "20" },
        { paymentId: second, paymentDate: "2026-09-03", paidAmount: "30" },
      ],
      progress: "PARTLY_PAID",
    },
  },
});
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
  await userEvent.setup().click(screen.getAllByRole("button", { name: "Remove" })[0]!);
  expect(onAct).toHaveBeenCalledExactlyOnceWith("REMOVE_PAYMENT", second);
});
it("offers no removal without the supplied offered-act flag", () => {
  render(
    <CasePayments
      region={supplied}
      current={{ ...current, advice: { ...caseAdvice([]), history: "FULL" } }}
      onAct={vi.fn()}
    />,
  );
  expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
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
