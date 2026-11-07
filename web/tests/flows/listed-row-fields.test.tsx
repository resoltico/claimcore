import { expect, it } from "vitest";
import { TableView } from "../../src/ui/TableView";
import { render, screen } from "../support/presentation-test-support";

it("renders a supplied additional date without consulting business field declarations", () => {
  render(
    <TableView
      view={{
        kind: "TABLE",
        columns: [
          {
            key: "future-date",
            labelId: "field.incidentDate.label",
            prominence: "PRIMARY",
            isMoney: false,
          },
        ],
        rows: [[{ kind: "DATE", value: "2026-11-04" }]],
      }}
    />,
  );
  expect(screen.getByRole("columnheader", { name: "Incident date" })).toBeVisible();
  expect(screen.getByText("04/11/2026")).toHaveAttribute("dateTime", "2026-11-04");
});
it("keeps headers for an empty page", () => {
  render(
    <TableView
      view={{
        kind: "TABLE",
        columns: [
          {
            key: "future-value",
            labelId: "field.incidentDate.label",
            prominence: "PRIMARY",
            isMoney: false,
          },
        ],
        rows: [],
      }}
    />,
  );
  expect(screen.getByRole("columnheader", { name: "Incident date" })).toBeVisible();
  expect(screen.getAllByRole("row")).toHaveLength(1);
});
