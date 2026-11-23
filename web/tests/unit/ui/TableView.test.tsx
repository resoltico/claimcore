import userEvent from "@testing-library/user-event";
import { selectRowAction } from "../../support/workspace-actions";
import { operationAffordance } from "../../support/affordances.fixtures";
import { expect, it } from "vitest";
import { DataTable } from "../../../src/ui/DataTable";
import { render, screen } from "../../support/presentation-test-support";

it("renders supplied columns and cells without field knowledge", async () => {
  const rendered = render(
    <DataTable
      onAction={() => undefined}
      view={{
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "future-text",
            labelId: "field.policyNumber.label",
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
            prominence: "SECONDARY",
            isMoney: false,
          },
          {
            key: "future-detail",
            labelId: "field.operationId.label",
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
              { kind: "IDENTIFIER", value: "never-in-list" },
            ],
          ] as const
        ).map((values, index) => ({
          key: String(index),
          actions: [
            operationAffordance("case.close", {
              kind: "CASE",
              target: { kind: "REFERENCE", caseReference: "OPAQUE" },
              expectedRevision: 3,
            }),
          ],
          values,
          detail: { summary: null, highlights: [], sections: [] },
        })),
      }}
    />,
  );
  expect(screen.getAllByRole("columnheader")).toHaveLength(4);
  expect(screen.getAllByRole("cell")).toHaveLength(4);
  expect(screen.queryByRole("columnheader", { name: "Operation ID" })).toBeNull();
  expect(screen.queryByText("never-in-list")).toBeNull();
  expect(screen.getAllByRole("columnheader").at(-1)).toHaveTextContent("Available actions");
  await selectRowAction(
    userEvent.setup(),
    screen.getByRole("row", { name: /العربية/u }),
    "Close the case",
  );
  expect(screen.getAllByText("Not recorded").length).toBeGreaterThan(0);
  expect(rendered.container.querySelector('bdi[dir="auto"]')).toHaveTextContent("العربية");
  expect(rendered.container.querySelector('bdi[dir="ltr"]')).toHaveTextContent("0001 EUR");
  expect(rendered.container.querySelector("script")).toBeNull();
  expect(screen.getByRole("table", { name: "Results" })).toBeVisible();
});
