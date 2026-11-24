import { expect, it } from "vitest";
import { DataTable } from "../../src/ui/DataTable";
import { render, screen } from "../support/presentation-test-support";

it("renders a supplied additional date without consulting business field declarations", () => {
  render(
    <DataTable
      view={{
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "future-date",
            labelId: "field.incidentDate.label",
            prominence: "PRIMARY",
            joins: null,
            isMoney: false,
          },
        ],
        rows: ([[{ kind: "DATE", value: "2026-11-04" }]] as const).map((values, index) => ({
          key: String(index),
          values,
        })),
      }}
    />,
  );
  expect(screen.getByRole("columnheader", { name: "Incident date" })).toBeVisible();
  expect(screen.getByText("04/11/2026")).toHaveAttribute("dateTime", "2026-11-04");
});
it("keeps headers for an empty page", () => {
  render(
    <DataTable
      view={{
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "future-value",
            labelId: "field.incidentDate.label",
            prominence: "PRIMARY",
            joins: null,
            isMoney: false,
          },
        ],
        rows: ([] as const).map((values, index) => ({
          key: String(index),
          values,
        })),
      }}
    />,
  );
  expect(screen.getByRole("columnheader", { name: "Incident date" })).toBeVisible();
  expect(screen.getAllByRole("row")).toHaveLength(1);
});
