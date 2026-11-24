import { act } from "@testing-library/react";
import { useState } from "react";
import { expect, it, vi } from "vitest";
import * as navigation from "../../../../src/routing/navigation";
import { parseWorklistQuery, type WorklistQuery } from "../../../../src/routing/worklistQuery";
import { CaseSelectionRecovery } from "../../../../src/views/worklist/CaseSelectionRecovery";
import { WorklistToolbar } from "../../../../src/views/worklist/WorklistToolbar";
import { fireEvent, render, screen } from "../../../support/presentation-test-support";

it("keeps intermediate search text focused and publishes only an admitted draft", () => {
  vi.useFakeTimers();
  try {
    const publish = vi.fn();
    const query: WorklistQuery = { view: "ALL", status: ["OPENED"], text: "Earlier" };
    const Toolbar = () => {
      const [draft, setDraft] = useState("Earlier");
      return (
        <WorklistToolbar query={query} search={draft} onSearch={setDraft} onChange={publish} />
      );
    };
    render(<Toolbar />);
    const input = screen.getByRole("searchbox", { name: /Filter this view/u });
    input.focus();
    fireEvent.change(input, { target: { value: "North " } });
    act(() => {
      vi.advanceTimersByTime(700);
    });

    expect(publish).not.toHaveBeenCalled();
    expect(input).toHaveFocus();
    expect(input).toHaveValue("North ");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
    expect(publish).not.toHaveBeenCalled();
    expect(input).toHaveFocus();
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByRole("alert")).toHaveTextContent("Remove spaces at the start or end.");

    fireEvent.change(input, { target: { value: "North Star" } });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(700);
    });
    expect(publish).not.toHaveBeenCalled();
    fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
    expect(publish).toHaveBeenCalledExactlyOnceWith({
      view: "ALL",
      status: ["OPENED"],
      text: "North Star",
    });
  } finally {
    vi.useRealTimers();
  }
});

it("applies the whole date pair once and associates a reversed range with its end", () => {
  const publish = vi.fn();
  render(
    <WorklistToolbar
      query={{ receivedFrom: "2026-09-01", receivedThrough: "2026-09-08" }}
      search=""
      onSearch={vi.fn()}
      onChange={publish}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u }));
  fireEvent.change(screen.getByLabelText("Received from"), { target: { value: "2026-09-09" } });
  expect(publish).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  expect(screen.getByLabelText("Received through")).toHaveAttribute("aria-invalid", "true");
  expect(publish).not.toHaveBeenCalled();
  fireEvent.change(screen.getByLabelText("Received through"), { target: { value: "2026-09-12" } });
  fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  expect(publish).toHaveBeenCalledExactlyOnceWith({
    receivedFrom: "2026-09-09",
    receivedThrough: "2026-09-12",
  });
});

it("requires an explicit duplicate choice and applies only the corrected chosen value", () => {
  const admitted = parseWorklistQuery(
    "?view=ALL&status=OPENED&receivedFrom=2026-09-02&receivedFrom=2026-09-04",
  );
  if (admitted.kind !== "invalid") {
    throw new Error("The duplicate scalar fixture must be invalid.");
  }
  const navigate = vi.spyOn(navigation, "navigate").mockReturnValue(true);
  try {
    render(<CaseSelectionRecovery admission={admitted} />);
    const apply = screen.getByRole("button", { name: "Apply correction" });
    expect(apply).toBeDisabled();
    for (const option of screen.getAllByRole("radio")) {
      expect(option).not.toBeChecked();
    }
    expect(navigate).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("radio", { name: "2026-09-04" }));
    const value = screen.getByRole("textbox", { name: "Received from" });
    expect(value).toHaveValue("2026-09-04");
    fireEvent.change(value, { target: { value: "" } });
    expect(apply).toBeDisabled();
    fireEvent.change(value, { target: { value: "2026-09-03" } });
    fireEvent.click(apply);
    expect(navigate).toHaveBeenCalledExactlyOnceWith(
      "/cases?view=ALL&status=OPENED&receivedFrom=2026-09-03",
    );
  } finally {
    navigate.mockRestore();
  }
});
