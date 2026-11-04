import { useState } from "react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "@testing-library/react";
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
