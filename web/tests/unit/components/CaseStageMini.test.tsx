import { expect, it } from "vitest";
import { render } from "../../support/presentation-test-support";
import { CaseStageMini } from "../../../src/components/CaseStageMini";
it("renders only the core-supplied completed stages and current marker", () => {
  const view = render(
    <CaseStageMini value={{ current: "CLOSED", completed: ["OPENED", "CLOSED"] }} />,
  );
  const steps = view.container.querySelectorAll("li");
  expect(steps).toHaveLength(4);
  expect(steps[0]).toHaveAttribute("data-complete", "true");
  expect(steps[1]).toHaveAttribute("data-complete", "false");
  expect(steps[2]).toHaveAttribute("data-complete", "false");
  expect(steps[3]).toHaveAttribute("data-complete", "true");
  expect(steps[3]).toHaveAttribute("aria-current", "step");
});
it("omits the outline when the core provides none", () => {
  const view = render(<CaseStageMini value={null} />);
  expect(view.container.childElementCount).toBe(0);
});
