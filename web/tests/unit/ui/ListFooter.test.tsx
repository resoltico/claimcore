import { expect, it, vi } from "vitest";
import { ListFooter } from "../../../src/ui/ListFooter";
import { render, screen } from "../../support/presentation-test-support";
import { workBasis } from "../../support/inputs";

it("uses plural-safe shown counts and offers continuation only when supplied", () => {
  const props = {
    subject: "cases" as const,
    basis: workBasis,
    loading: false,
    load: vi.fn(),
    refresh: vi.fn(),
  };
  const { rerender } = render(<ListFooter {...props} count={1} continuation={null} />);
  expect(screen.getByText("1 case shown")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  rerender(<ListFooter {...props} count={2} continuation="next" />);
  expect(screen.getByText("2 cases shown")).toBeVisible();
  expect(screen.getByRole("button", { name: "Load more" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Refresh" })).toBeVisible();
});
