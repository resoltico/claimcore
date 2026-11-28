import { expect, it } from "vitest";
import { Stepper } from "../../../src/ui/Stepper";
import { render } from "../../support/presentation-test-support";

it("renders coverage and decorative connectors without a second meter", () => {
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
            date: null,
            value: { kind: "MESSAGE", messageId: "label.payableCovered" },
          },
          { stage: "CLOSED", reached: false, date: null, value: null },
        ],
      }}
    />,
  );
  expect(
    container.querySelectorAll('li > .stage-connector[aria-hidden="true"] + strong'),
  ).toHaveLength(3);
  expect(container.querySelectorAll("meter")).toHaveLength(0);
  expect(container.querySelector('li[aria-current="step"] strong')).toBeInTheDocument();
});
