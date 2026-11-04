import { expect, it } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { CaseProgress } from "../../../../src/views/case/CaseProgress";
it("renders the supplied nonlinear tracker without deriving completion, dates or amounts", () => {
  const view = render(
    <CaseProgress
      value={{
        kind: "TRACKER",
        current: "CLOSED",
        steps: [
          {
            stage: "OPENED",
            completed: true,
            date: "2026-10-31",
            amount: { amount: "100", currency: "USD" },
          },
          { stage: "DECIDED", completed: false, date: null, amount: null },
          { stage: "PAID", completed: false, date: null, amount: null },
          { stage: "CLOSED", completed: true, date: "2026-11-04", amount: null },
        ],
      }}
    />,
  );
  expect(view.container.querySelectorAll("li")).toHaveLength(4);
  expect(screen.getByText("Closed", { exact: true }).closest("li")).toHaveAttribute(
    "aria-current",
    "step",
  );
  expect(view.container.querySelectorAll("li[data-complete=true]")).toHaveLength(2);
  expect(view.container).toHaveTextContent("31/10/2026");
  expect(view.container).toHaveTextContent("04/11/2026");
  expect(view.container).toHaveTextContent("100.00 USD");
  expect(view.container.querySelector("li:nth-child(2)")).toHaveTextContent("Not recorded");
  expect(view.container.querySelector("li:nth-child(3)")).toHaveTextContent("Not recorded");
});
it.each(["REINSTATED", "VOIDED", "ERASED"] as const)(
  "uses the supplied %s status instead of a tracker",
  (kind) => {
    const view = render(<CaseProgress value={{ kind }} />);
    expect(view.container.querySelector("ol")).toBeNull();
    expect(screen.getByRole("status")).toBeVisible();
  },
);
