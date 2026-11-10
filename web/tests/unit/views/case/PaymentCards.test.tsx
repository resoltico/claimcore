import { within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import type { RegionContent } from "@contracts/client";
import { PaymentRows } from "../../../../src/views/case/PaymentRows";
vi.mock("../../../../src/hooks/useCompact", () => ({ useCompact: () => true }));
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
it("renders narrow payment cards from supplied columns and keeps technical identity in Details", async () => {
  const remove = vi.fn();
  render(
    <PaymentRows
      content={content}
      handles={[{ paymentId, canRemove: true }]}
      zone="UTC"
      onRemove={remove}
    />,
  );
  const card = within(screen.getByRole("list")).getByRole("listitem");
  expect(within(card).getByText("Payment date")).toBeVisible();
  expect(within(card).getByText("Paid amount")).toBeVisible();
  expect(within(card).getByText("77.00 USD")).toBeVisible();
  expect(within(card).getByText(paymentId)).not.toBeVisible();
  await userEvent.click(within(card).getByText("Record details", { selector: "summary" }));
  expect(within(card).getByText(paymentId)).toBeVisible();
  await userEvent.click(within(card).getByRole("button", { name: "Remove payment entry" }));
  expect(remove).toHaveBeenCalledExactlyOnceWith(paymentId);
});
