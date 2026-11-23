import { within } from "@testing-library/react";
import { expect, it } from "vitest";
import userEvent from "@testing-library/user-event";
import { PresentedValue } from "../../../src/components/PresentedValue";
import type { RegionContent } from "@contracts/client";
import { DataTable } from "../../../src/ui/DataTable";
import { render, screen } from "../../support/presentation-test-support";

const view: Extract<RegionContent, { kind: "TABLE" }> = {
  kind: "TABLE",
  columns: [
    {
      key: "reference",
      labelId: "field.caseReference.label",
      prominence: "PRIMARY",
      isMoney: false,
    },
    { key: "due", labelId: "field.reminderDueDate.label", prominence: "SECONDARY", isMoney: false },
    {
      key: "revision",
      labelId: "field.expectedRevision.label",
      prominence: "DETAIL",
      isMoney: false,
    },
  ],
  rows: [
    {
      key: "case",
      values: [
        { kind: "REFERENCE", value: "CASE-1" },
        { kind: "ABSENT", reasonId: "label.notRecorded" },
        { kind: "IDENTIFIER", value: "unlisted-revision" },
      ],
      detail: {
        summary: null,
        highlights: [],
        sections: [
          {
            titleId: "label.details",
            entries: [
              {
                key: "private",
                labelId: "label.reason",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "Authorized retained row detail" },
              },
            ],
          },
        ],
      },
    },
  ],
};

it("lists primary and secondary cells, preserves the absent value label and omits detail columns", () => {
  const { container } = render(<DataTable view={view} />);
  expect(screen.getAllByRole("columnheader")).toHaveLength(3);
  expect(screen.queryByText("unlisted-revision")).not.toBeInTheDocument();
  const stacked = container.querySelector(".data-table-stack");
  expect(stacked).toHaveTextContent("Due date");
  expect(stacked).toHaveTextContent("Not recorded");
  expect(stacked?.querySelector('[aria-hidden="true"]')).toHaveTextContent("—");
});

it("shows the selected row's details in a dialog and removes them when the read withdraws the row", async () => {
  const user = userEvent.setup();
  const { rerender } = render(<DataTable view={view} />);
  await user.click(screen.getByRole("button", { name: "Actions for CASE-1" }));
  await user.click(screen.getByRole("menuitem", { name: "View details" }));
  expect(
    within(screen.getByRole("dialog")).getByText("Authorized retained row detail"),
  ).toBeVisible();
  rerender(<DataTable view={{ ...view, rows: [] }} />);
  expect(screen.queryByText("Authorized retained row detail")).not.toBeInTheDocument();
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it.each([
  ["OVERDUE", "Overdue", "danger"],
  ["TODAY", "Today", "warning"],
  ["UPCOMING", "Upcoming", "info"],
] as const)(
  "presents the supplied %s due code with the date and its tone",
  (token, label, tone) => {
    const { container } = render(
      <PresentedValue
        value={{
          kind: "DATE_WITH_CODE",
          value: "2026-11-01",
          code: { kind: "CODE", vocabulary: "reminderDue", token },
        }}
        timeZone="UTC"
      />,
    );
    expect(container.querySelector("time")).toHaveAttribute("datetime", "2026-11-01");
    expect(screen.getByText(label)).toHaveAttribute("data-tone", tone);
  },
);
