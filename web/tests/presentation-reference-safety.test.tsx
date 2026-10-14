import { it, expect } from "vitest";
import { render, screen } from "./presentation-test-support";
import { ReferenceSummary } from "../src/components/ReferenceSummary";
it("exact references isolate Unicode and never interpret markup", () => {
  const reference = "A\u0308 العربية\u2069\u202E\u200D <script>";
  render(<ReferenceSummary id="ui.exactReference" values={{ label: "Reference", reference }} />);
  expect([...document.querySelectorAll("bdi")].some((x) => x.textContent === reference)).toBe(true);
  expect(document.querySelector("script")).toBeNull();
  expect(screen.getByText(/U\+2069/u)).toBeVisible();
});
