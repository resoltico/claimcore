import { expect, it } from "vitest";
import { Table } from "../../../src/ui/regions/Table";
import { render, screen } from "../../support/presentation-test-support";

it("renders supplied columns and cells without field knowledge", () => {
  const rendered = render(
    <Table
      renderActions={(key) => <button>Act {key}</button>}
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
          detail: { summary: null, highlights: [], sections: [] },
        })),
      }}
    />,
  );
  expect(screen.getAllByRole("columnheader")).toHaveLength(5);
  expect(screen.getAllByRole("cell")).toHaveLength(5);
  expect(screen.getAllByRole("columnheader").at(-1)).toHaveTextContent("Available actions");
  expect(
    screen
      .getAllByRole("cell")
      .at(-1)
      ?.contains(screen.getByRole("button", { name: "Act 0" })),
  ).toBe(true);
  expect(screen.getByText("Not recorded")).toBeVisible();
  expect(rendered.container.querySelector('bdi[dir="auto"]')).toHaveTextContent("العربية");
  expect(rendered.container.querySelector('bdi[dir="ltr"]')).toHaveTextContent("0001 EUR");
  expect(rendered.container.querySelector("script")).toBeNull();
  expect(screen.getByRole("group", { name: "Results" })).toHaveAttribute("tabindex", "0");
});
