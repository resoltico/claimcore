import { it, expect } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { LifecycleStatus } from "../../../src/views/LifecycleStatus";
const labels = { REINSTATED: "Case reinstated", VOIDED: "Voided", ERASED: "Erased" } as const;
it.each(["VOIDED", "ERASED", "REINSTATED"] as const)(
  "renders the core-selected %s status",
  (kind) => {
    render(<LifecycleStatus value={{ kind }} />);
    expect(screen.getByRole("status")).toHaveTextContent(labels[kind]);
  },
);
it("does not duplicate a tracker with a lifecycle panel", () => {
  render(
    <LifecycleStatus
      value={{
        kind: "TRACKER",
        current: "OPENED",
        steps: [
          {
            stage: "OPENED",
            completed: true,
            date: "2026-09-01",
            amount: { amount: "12.34", currency: "EUR" },
          },
          { stage: "DECIDED", completed: false, date: null, amount: null },
          { stage: "PAID", completed: false, date: null, amount: null },
          { stage: "CLOSED", completed: false, date: null, amount: null },
        ],
      }}
    />,
  );
  expect(screen.queryByRole("status")).toBeNull();
});
