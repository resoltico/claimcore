import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseHeadline } from "../../../src/components/CaseHeadline";
import { CaseAttention } from "../../../src/components/CaseAttention";
it.each([
  ["ERASED", "Erased"],
  ["VOIDED", "Voided"],
  ["CLOSED", "Closed"],
  ["PAID", "Paid"],
  ["DECIDED", "Decided"],
  ["UNDECIDED", "Undecided"],
] as const)("renders the core %s headline", (value, label) => {
  render(<CaseHeadline value={value} />);
  expect(screen.getByText(label)).toHaveAttribute("data-headline", value);
});
it("preserves explicit empty attention without inferring a missing plan or assignee", () => {
  const view = render(<CaseAttention values={[]} />);
  expect(view.container.childElementCount).toBe(0);
});
