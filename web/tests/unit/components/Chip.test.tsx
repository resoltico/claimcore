import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { ChipList } from "../../../src/ui/ChipList";

it("renders the core headline detail and attention with their declared tones", () => {
  render(
    <ChipList
      values={[
        {
          vocabulary: "headline",
          token: "CLOSED",
          detail: { kind: "CODE", vocabulary: "closureOutcome", token: "SETTLED" },
        },
        { vocabulary: "attention", token: "DEADLINE_OVERDUE" },
        { vocabulary: "attention", token: "READY_TO_CLOSE" },
      ]}
    />,
  );
  expect(screen.getByText("Closed: Settled")).toHaveAttribute("data-tone", "neutral");
  expect(screen.getByText("Overdue deadline")).toHaveAttribute("data-tone", "danger");
  expect(screen.getByText("Ready to close")).toHaveAttribute("data-tone", "success");
});

it("preserves an empty attention list", () => {
  expect(render(<ChipList values={[]} />).container.childElementCount).toBe(0);
});
