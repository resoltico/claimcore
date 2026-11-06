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
it("offers a line break before the whole year while preserving full numeric date and amount text", () => {
  const view = render(
    <CaseProgress
      value={{
        kind: "TRACKER",
        current: "OPENED",
        steps: [
          {
            stage: "OPENED",
            completed: true,
            date: "0001-01-01",
            amount: { amount: "123456789012345678.0100", currency: "EUR" },
          },
          { stage: "DECIDED", completed: false, date: null, amount: null },
          { stage: "PAID", completed: false, date: null, amount: null },
          { stage: "CLOSED", completed: false, date: null, amount: null },
        ],
      }}
    />,
  );
  const time = view.container.querySelector("time")!;
  expect(time).toHaveAttribute("datetime", "0001-01-01");
  expect(time.textContent).toBe("01/01/0001");
  expect(time.querySelector("wbr + .case-stage-year")).toHaveTextContent("0001");
  expect(time.querySelectorAll("wbr")).toHaveLength(1);
  expect(view.container).toHaveTextContent("123,456,789,012,345,678.0100 EUR");
});
