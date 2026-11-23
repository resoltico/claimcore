import { selectRowAction } from "../../../support/workspace-actions";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import type { RegionContent } from "@contracts/client";
import { PaymentRows } from "../../../../src/views/case/PaymentRows";
const paymentId = "10000000-0000-7000-8000-000000000090";
const content: Extract<RegionContent, { kind: "TABLE" }> = {
  kind: "TABLE",
  columns: [
    {
      key: "paymentDate",
      labelId: "field.paymentDate.label",
      prominence: "PRIMARY",
      isMoney: false,
    },
    { key: "paidAmount", labelId: "field.paidAmount.label", prominence: "PRIMARY", isMoney: true },
  ],
  rows: [
    {
      key: paymentId,
      actions: [
        operationAffordance("case.removePayment", {
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: "CASE-1" },
          expectedRevision: 1,
          paymentId,
        }),
      ],
      values: [
        { kind: "DATE", value: "2026-09-03" },
        { kind: "MONEY", amount: "77", currency: "USD", displayScale: 2 },
      ],
      detail: {
        summary: null,
        highlights: [],
        sections: [
          {
            titleId: "label.recordDetails",
            entries: [
              {
                key: "paymentId",
                labelId: "field.paymentId.label",
                prominence: "DETAIL",
                value: { kind: "IDENTIFIER", value: paymentId },
              },
            ],
          },
        ],
      },
    },
  ],
};
it("renders supplied payment columns and keeps the technical identity in the details dialog", async () => {
  const remove = vi.fn();
  render(<PaymentRows content={content} zone="UTC" onAction={remove} />);
  const user = userEvent.setup();
  const table = screen.getByRole("table");
  const row = within(table).getByRole("row", { name: /77[.]00 USD/u });
  expect(within(table).getByRole("columnheader", { name: "Payment date" })).toBeVisible();
  expect(within(table).getByRole("columnheader", { name: "Paid amount" })).toBeVisible();
  expect(within(row).getByText("77.00 USD")).toBeVisible();
  expect(screen.queryByText(paymentId)).toBeNull();
  await selectRowAction(user, row, "View details");
  expect(within(screen.getByRole("dialog")).getByText(paymentId)).toBeVisible();
  await user.keyboard("{Escape}");
  await selectRowAction(user, row, "Remove payment entry");
  expect(remove).toHaveBeenCalledExactlyOnceWith(content.rows[0]!.actions![0]);
});
