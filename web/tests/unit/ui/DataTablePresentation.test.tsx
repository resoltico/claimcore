import { within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { IdentifierValue } from "../../../src/components/IdentifierValue";
import { PresentedValue } from "../../../src/components/PresentedValue";
import type { RegionContent } from "@contracts/client";
import { DataTable } from "../../../src/ui/DataTable";
import { render, screen } from "../../support/presentation-test-support";
import { operationAffordance } from "../../support/affordances.fixtures";

const view: Extract<RegionContent, { kind: "TABLE" }> = {
  kind: "TABLE",
  columns: [
    {
      key: "reference",
      labelId: "field.caseReference.label",
      prominence: "PRIMARY",
      joins: null,
      isMoney: false,
    },
    {
      key: "due",
      labelId: "field.reminderDueDate.label",
      prominence: "SECONDARY",
      joins: null,
      isMoney: false,
    },
    {
      key: "revision",
      labelId: "field.expectedRevision.label",
      prominence: "DETAIL",
      joins: null,
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
  expect(stacked?.textContent).toBe("");
  const due = container.querySelector('[data-column="due"]');
  expect(due).toHaveTextContent("Not recorded");
  expect(due?.querySelector('[aria-hidden="true"]')).toHaveTextContent("—");
});

it("uses the supplied empty message for a table section with no recorded entries", () => {
  render(
    <DataTable
      view={{
        ...view,
        sections: [
          {
            titleId: "label.paymentDecision",
            emptyId: "empty.paymentDecision",
            entries: [
              {
                key: "paymentDecisionDate",
                labelId: "field.paymentDecisionDate.label",
                prominence: "PRIMARY",
                value: { kind: "ABSENT", reasonId: "label.notRecorded" },
                recorded: false,
              },
            ],
          },
        ],
      }}
    />,
  );
  const decision = within(screen.getByRole("group", { name: "Payment decision" }));
  expect(decision.getByText("No payment decision yet")).toBeVisible();
  expect(decision.queryByText("Not recorded")).not.toBeInTheDocument();
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

it("keeps blocked row actions in the menu with visible and accessible core advice", async () => {
  const user = userEvent.setup();
  const onAction = vi.fn();
  const binding = {
    kind: "CASE",
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
    expectedRevision: 1,
  } as const;
  const action = operationAffordance("case.close", binding);
  const blocked = {
    ...action,
    blocked: {
      action: "ASK_FOR_ACCESS",
      message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
    },
  } as const;
  const row = {
    ...view.rows[0]!,
    actions: [blocked, operationAffordance("case.decide", binding)],
  };
  render(<DataTable view={{ ...view, rows: [row] }} onAction={onAction} />);
  await user.click(screen.getByRole("button", { name: "Actions for CASE-1" }));
  const close = screen.getByRole("menuitem", { name: "Close the case" });
  const advice = "This item does not exist, or you do not have access to it.";
  expect(close).toHaveAttribute("aria-disabled", "true");
  expect(close).toHaveAccessibleDescription(advice);
  expect(within(close).getByText(advice)).toBeVisible();
  expect(screen.getAllByRole("menuitem")).toHaveLength(3);
  await user.click(close);
  expect(onAction).not.toHaveBeenCalled();
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

it("keeps the exact identifier in the link and offers breaks after separator runs", () => {
  const value = "CASE--PART/_NUMBER:17";
  const { container } = render(
    <a href={`/cases/reference/${encodeURIComponent(value)}`}>
      <IdentifierValue value={value} />
    </a>,
  );
  const link = screen.getByRole("link", { name: value });
  expect(link.textContent).toBe(value);
  expect(link).toHaveAttribute("href", `/cases/reference/${encodeURIComponent(value)}`);
  expect(
    [...container.querySelectorAll("wbr")].map((point) => point.previousSibling?.textContent),
  ).toEqual(["--", "/_", ":"]);
});

it("adds amount break points between exact digit groups", () => {
  const { container } = render(
    <PresentedValue
      value={{
        kind: "MONEY",
        amount: "123456789012345678.1234",
        currency: "EUR",
        displayScale: 4,
      }}
      timeZone="UTC"
    />,
  );
  const amount = container.querySelector(".money-value");
  expect(amount?.textContent).toBe("123,456,789,012,345,678.1234 EUR");
  expect(
    [...amount!.querySelectorAll("wbr")].map((point) => point.previousSibling?.textContent),
  ).toEqual([",", ",", ",", ",", ","]);
});

it("puts declared joins in the target cell and prefixes a joined date", () => {
  const { container } = render(
    <DataTable
      view={{
        kind: "TABLE",
        columns: [
          {
            key: "status",
            labelId: "label.caseState",
            prominence: "PRIMARY",
            isMoney: false,
            joins: null,
          },
          {
            key: "attention",
            labelId: "label.attention",
            prominence: "PRIMARY",
            isMoney: false,
            joins: "status",
          },
          {
            key: "due",
            labelId: "field.reminderDueDate.label",
            prominence: "SECONDARY",
            isMoney: false,
            joins: "status",
          },
        ],
        rows: [
          {
            key: "row",
            values: [
              { kind: "STATE", vocabulary: "headline", token: "UNDECIDED" },
              { kind: "CODES", vocabulary: "attention", tokens: ["DEADLINE_OVERDUE"] },
              { kind: "DATE", value: "2026-11-24" },
            ],
          },
        ],
      }}
    />,
  );
  expect(screen.getAllByRole("columnheader")).toHaveLength(1);
  expect(container.querySelectorAll('[data-column="status"] .chip-list > li')).toHaveLength(2);
  expect(
    container.querySelector('[data-column="status"] [data-kind="DATE"] .sr-only'),
  ).toHaveTextContent("Due date");
});
