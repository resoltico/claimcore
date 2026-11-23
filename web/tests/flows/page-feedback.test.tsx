import { mockEndpoint } from "../support/endpoint-mocks";
import { CaseTimeline } from "../../src/views/case/CaseTimeline";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { ListFooter } from "../../src/ui/ListFooter";
import { workBasis } from "../support/inputs";
it("an empty nonterminal page keeps its issued continuation available", () => {
  const props = {
    subject: "cases" as const,
    count: 0,
    basis: workBasis,
    loading: false,
    load: vi.fn(),
    refresh: vi.fn(),
  };
  const { rerender } = render(<ListFooter {...props} continuation="synthetic-continuation" />);
  expect(screen.getByText("0 cases shown")).toBeVisible();
  expect(screen.getByRole("button", { name: "Load more" })).toBeEnabled();
  rerender(<ListFooter {...props} continuation={null} />);
  expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  expect(screen.queryByText("All results shown.")).toBeNull();
});
afterEach(() => {
  vi.restoreAllMocks();
});
it("a failed history read stops loading without inventing a successful empty page", async () => {
  mockEndpoint("case.timeline").mockResolvedValue({
    kind: "deliveryFailure",
    notice: {
      kind: "local",
      reason: "unreachable",
    },
    dispatched: false,
  });
  render(<CaseTimeline target={{ kind: "REFERENCE", caseReference: "SYNTHETIC" }} />);
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(screen.queryByRole("status")).toBeNull();
  expect(screen.queryByText("No activity to show.")).toBeNull();
  expect(screen.queryByText("All results shown.")).toBeNull();
});
