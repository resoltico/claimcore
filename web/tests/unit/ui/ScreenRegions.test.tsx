import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { ScreenView } from "../../../src/ui/ScreenView";

it("renders an opaque core document without reconstructing its facts", () => {
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
  const technical = screen.getByText("opaque-operation-identity");
  expect(technical.closest("details")).not.toHaveAttribute("open");
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
                { key: "first-id", values: [{ kind: "TEXT", value: "First" }] },
                { key: "second-id", values: [{ kind: "TEXT", value: "Second" }] },
              ],
              groups: [
                { titleId: "reminderDue.TODAY.label", count: 1, rowKeys: ["second-id"] },
                { titleId: "reminderDue.OVERDUE.label", count: 1, rowKeys: ["first-id"] },
              ],
            },
          },
        ],
      }}
      renderActions={(key) => (
        <button
          onClick={() => {
            selected(key);
          }}
        >
          Choose {key}
        </button>
      )}
    />,
  );
  await userEvent.setup().click(screen.getByRole("button", { name: "Choose second-id" }));
  expect(selected).toHaveBeenCalledOnce();
  expect(selected).toHaveBeenCalledWith("second-id");
  expect(
    screen
      .getByRole("group", { name: "Second" })
      .contains(screen.getByRole("button", { name: "Choose second-id" })),
  ).toBe(true);
});
