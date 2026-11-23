import { rowActions, selectRowAction } from "../../support/workspace-actions";
import { operationAffordance } from "../../support/affordances.fixtures";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { ScreenView } from "../../../src/ui/ScreenView";

it("renders an opaque core document without reconstructing its facts", async () => {
  const view = render(
    <ScreenView
      view={{
        regions: [
          {
            role: "FACTS",
            content: {
              kind: "ENTRIES",
              entries: [
                {
                  key: "authored-fact",
                  labelId: "label.details",
                  prominence: "PRIMARY",
                  value: { kind: "TEXT", value: "Core-only caption Ω" },
                },
              ],
            },
          },
          {
            role: "DETAILS",
            content: {
              kind: "ENTRIES",
              entries: [
                {
                  key: "technical-identity",
                  labelId: "field.operationId.label",
                  prominence: "DETAIL",
                  value: { kind: "IDENTIFIER", value: "opaque-operation-identity" },
                },
              ],
            },
          },
        ],
      }}
    />,
  );
  expect(screen.getByText("Core-only caption Ω")).toBeVisible();
  expect(screen.queryByText("opaque-operation-identity")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Technical details" }));
  expect(screen.getByText("opaque-operation-identity").closest('[role="dialog"]')).not.toBeNull();
  expect(view.container.querySelector('[data-region="FACTS"]')).not.toHaveTextContent(
    "opaque-operation-identity",
  );
});

it("renders only supplied groups in their order with stable row identities", () => {
  const view = render(
    <ScreenView
      view={{
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                { key: "future", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
              ],
              rows: [
                { key: "a", values: [{ kind: "TEXT", value: "First stored" }] },
                { key: "b", values: [{ kind: "TEXT", value: "Second stored" }] },
              ],
              groups: [
                { titleId: "reminderDue.TODAY.label", count: 1, rowKeys: ["b"] },
                { titleId: "reminderDue.OVERDUE.label", count: 1, rowKeys: ["a"] },
              ],
            },
          },
        ],
      }}
    />,
  );
  const rows = view.container.querySelectorAll("tbody tr:not(.table-group)");
  expect(rows[0]).toHaveTextContent("Second stored");
  expect(rows[1]).toHaveTextContent("First stored");
});
it("offers no copy controls for ordinary facts", () => {
  render(
    <ScreenView
      view={{
        regions: [
          {
            role: "FACTS",
            content: {
              kind: "ENTRIES",
              entries: [
                {
                  key: "future",
                  labelId: "label.details",
                  prominence: "PRIMARY",
                  value: { kind: "REFERENCE", value: "CASE-1" },
                },
              ],
            },
          },
        ],
      }}
    />,
  );
  expect(screen.queryByRole("button")).toBeNull();
});

it("routes grouped row actions by supplied identity rather than visible position", async () => {
  const selected = vi.fn();
  render(
    <ScreenView
      view={{
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                { key: "name", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
              ],
              rows: [
                {
                  key: "first-id",
                  actions: [
                    operationAffordance("case.close", {
                      kind: "CASE",
                      target: { kind: "REFERENCE", caseReference: "OPAQUE" },
                      expectedRevision: 3,
                    }),
                  ],
                  values: [{ kind: "TEXT", value: "First" }],
                },
                {
                  key: "second-id",
                  actions: [
                    operationAffordance("case.close", {
                      kind: "CASE",
                      target: { kind: "REFERENCE", caseReference: "OPAQUE" },
                      expectedRevision: 3,
                    }),
                  ],
                  values: [{ kind: "TEXT", value: "Second" }],
                },
              ],
              groups: [
                { titleId: "reminderDue.TODAY.label", count: 1, rowKeys: ["second-id"] },
                { titleId: "reminderDue.OVERDUE.label", count: 1, rowKeys: ["first-id"] },
              ],
            },
          },
        ],
      }}
      onAction={(_action, row) => {
        selected(row!.key);
      }}
    />,
  );
  const second = screen.getByRole("row", { name: /Second/u });
  expect(screen.getAllByRole("button", { name: /^Actions for/u })[0]).toBe(rowActions(second));
  await selectRowAction(userEvent.setup(), second, "Close the case");
  expect(selected).toHaveBeenCalledExactlyOnceWith("second-id");
});
