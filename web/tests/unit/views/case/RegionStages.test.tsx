import { expect, it } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { RegionView } from "../../../../src/ui/regions/RegionView";
it("renders the supplied nonlinear tracker without deriving completion, dates or amounts", () => {
  const view = render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "CLOSED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: "2026-10-31",
              amount: { amount: "100", currency: "USD", displayScale: 2 },
            },
            { stage: "DECIDED", reached: false, date: null, amount: null },
            { stage: "PAID", reached: false, date: null, amount: null },
            { stage: "CLOSED", reached: true, date: "2026-11-04", amount: null },
          ],
        },
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
it("preserves the complete numeric date and exact amount display scale", () => {
  const view = render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "OPENED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: "0001-01-01",
              amount: { amount: "123456789012345678.0100", currency: "EUR", displayScale: 4 },
            },
            { stage: "DECIDED", reached: false, date: null, amount: null },
            { stage: "PAID", reached: false, date: null, amount: null },
            { stage: "CLOSED", reached: false, date: null, amount: null },
          ],
        },
      }}
    />,
  );
  const time = view.container.querySelector("time")!;
  expect(time).toHaveAttribute("datetime", "0001-01-01");
  expect(time.textContent).toBe("01/01/0001");
  expect(view.container).toHaveTextContent("123,456,789,012,345,678.0100 EUR");
});
