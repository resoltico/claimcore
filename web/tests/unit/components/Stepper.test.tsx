import { expect, it } from "vitest";
import { Stepper } from "../../../src/ui/Stepper";
import { render } from "../../support/presentation-test-support";

it("renders decorative connectors and leaves only the informative money meter", () => {
  const amount = { amount: "5", currency: "EUR", displayScale: 2 };
  const { container } = render(
    <Stepper
      timeZone="UTC"
      content={{
        kind: "STAGES",
        current: "PAID",
        steps: [
          { stage: "OPENED", reached: true, date: "2026-11-01", value: null },
          {
            stage: "PAID",
            reached: true,
            date: "2026-11-02",
            value: null,
            meter: {
              current: amount,
              target: amount,
              complete: true,
              description: {
                messageId: "label.paymentProgress",
                arguments: [
                  { key: "paid", value: { ...amount, kind: "MONEY" } },
                  { key: "payable", value: { ...amount, kind: "MONEY" } },
                ],
              },
            },
          },
          { stage: "CLOSED", reached: false, date: null, value: null },
        ],
      }}
    />,
  );
  expect(
    container.querySelectorAll('li > .stage-connector[aria-hidden="true"] + strong'),
  ).toHaveLength(3);
  expect(container.querySelectorAll("meter")).toHaveLength(1);
  expect(container.querySelector('li[aria-current="step"] .money-meter meter')).toBeInTheDocument();
});
