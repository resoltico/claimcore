import { useState } from "react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { ViewTabs } from "../../../src/ui/ViewTabs";
it("preserves core order, moves keyboard focus without dispatch and commits on Enter", async () => {
  const changed = vi.fn();
  const Example = () => {
    const [selected, setSelected] = useState("OVERDUE");
    return (
      <ViewTabs
        items={[
          { id: "OVERDUE", label: "Overdue", count: "7" },
          { id: "NEEDS_ATTENTION", label: "Needs attention", count: "2" },
        ]}
        selected={selected}
        label="Case view"
        loading={false}
        controls={<input aria-label="Filter" defaultValue="literal" />}
        onSelect={(value) => {
          changed(value);
          setSelected(value);
        }}
      >
        <output>Dataset</output>
      </ViewTabs>
    );
  };
  render(<Example />);
  const user = userEvent.setup();
  expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
    "Overdue 7",
    "Needs attention 2",
  ]);
  await user.tab();
  await user.keyboard("{ArrowRight}");
  expect(screen.getByRole("tab", { name: "Needs attention 2" })).toHaveFocus();
  expect(changed).not.toHaveBeenCalled();
  await user.keyboard("{Enter}");
  expect(changed).toHaveBeenCalledExactlyOnceWith("NEEDS_ATTENTION");
  await waitFor(() => {
    expect(screen.getByRole("tab", { name: "Needs attention 2" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });
  expect(screen.getByRole("textbox", { name: "Filter" })).toHaveValue("literal");
});

it("moves secondary views into the menu until one is selected", async () => {
  const Work = () => {
    const [selected, setSelected] = useState("ALL");
    return (
      <ViewTabs
        items={[
          { id: "ALL", label: "All", count: "8", prominence: "PRIMARY" },
          { id: "DONE", label: "Done", count: "3", prominence: "SECONDARY", menuLabel: "Done 3" },
        ]}
        selected={selected}
        label="Reminder view"
        loading={false}
        controls={null}
        onSelect={setSelected}
      >
        <output>Supplied reminders</output>
      </ViewTabs>
    );
  };

  render(<Work />);
  expect(screen.queryByRole("tab", { name: "Done 3" })).toBeNull();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "More views" }));
  await user.click(screen.getByRole("menuitem", { name: "Done 3" }));
  expect(screen.getByRole("tab", { name: "Done 3", selected: true })).toBeVisible();
  expect(screen.getByRole("tab", { name: "All 8", selected: false })).toBeVisible();
});
