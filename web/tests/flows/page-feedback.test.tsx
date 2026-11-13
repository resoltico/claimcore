import { mockEndpoint } from "../support/endpoint-mocks";
import { CaseTimeline } from "../../src/views/case/CaseTimeline";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { PageStatus } from "../../src/components/PageStatus";
it("an empty short nonterminal page never claims the end of visible scope", () => {
  const { rerender } = render(
    <PageStatus empty cursor="synthetic-continuation" emptyMessage="ui.noAccessibleCases" />,
  );
  expect(screen.getByText("No cases to show.")).toBeVisible();
  expect(screen.getByText("More results are available.")).toBeVisible();
  expect(screen.queryByText("All results shown.")).toBeNull();
  rerender(<PageStatus empty cursor={null} emptyMessage="ui.noAccessibleCases" />);
  expect(screen.getByText("No cases to show.")).toBeVisible();
  expect(screen.queryByText("All results shown.")).toBeNull();
  rerender(<PageStatus empty={false} cursor={null} emptyMessage="ui.noAccessibleCases" />);
  expect(screen.getByText("All results shown.")).toBeVisible();
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
