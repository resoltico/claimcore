import { expect, it } from "vitest";
import { TableView } from "../../../src/ui/TableView";
import { render, screen } from "../../support/presentation-test-support";

it("renders supplied columns and cells without field knowledge", () => {
  const rendered = render(
    <TableView
      view={{
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "future-text",
            labelId: "field.claimantName.label",
            prominence: "PRIMARY",
            isMoney: false,
          },
          {
            key: "future-money",
            labelId: "field.claimedAmount.label",
            prominence: "PRIMARY",
            isMoney: true,
          },
          {
            key: "future-absent",
            labelId: "field.paymentDate.label",
            prominence: "DETAIL",
            isMoney: false,
          },
        ],
        rows: (
          [
            [
              { kind: "TEXT", value: "العربية<script>literal</script>" },
              {
                kind: "MONEY",
                amount: "123456789012345678.0001",
                currency: "EUR",
                displayScale: 4,
              },
              { kind: "ABSENT", reasonId: "label.notRecorded" },
            ],
          ] as const
        ).map((values, index) => ({
          key: String(index),
          values,
        })),
      }}
    />,
  );
  expect(screen.getAllByRole("columnheader")).toHaveLength(3);
  expect(screen.getAllByRole("cell")).toHaveLength(3);
  expect(screen.getByText("Not recorded")).toBeVisible();
  expect(rendered.container.querySelector('bdi[dir="auto"]')).toHaveTextContent("العربية");
  expect(rendered.container.querySelector('bdi[dir="ltr"]')).toHaveTextContent("0001 EUR");
  expect(rendered.container.querySelector("script")).toBeNull();
  expect(screen.getByRole("group")).toHaveAttribute("tabindex", "0");
});
