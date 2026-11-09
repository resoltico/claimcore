import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { StateChip } from "../../../src/components/StateChip";
import { Codes } from "../../../src/ui/regions/Codes";
it.each([
  ["ERASED", "Erased"],
  ["VOIDED", "Voided"],
  ["CLOSED", "Closed"],
  ["PAID", "Paid"],
  ["DECIDED", "Decided"],
  ["UNDECIDED", "Undecided"],
] as const)("renders the core %s headline", (value, label) => {
  render(<StateChip value={value} />);
  expect(screen.getByText(label)).toHaveAttribute("data-headline", value);
});
it("preserves explicit empty attention without inferring a missing plan or assignee", () => {
  const view = render(<Codes content={{ kind: "CODES", vocabulary: "attention", tokens: [] }} />);
  expect(view.container.childElementCount).toBe(0);
});
