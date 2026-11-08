import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { ScreenView } from "../../../src/ui/ScreenView";
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
