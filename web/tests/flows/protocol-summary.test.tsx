import { CaseHistory } from "../../src/views/CaseHistory";
import { service } from "../../src/api/service";
import { productMetadata } from "../../src/protocol/productMetadata";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { ProtocolSummary } from "../../src/components/ProtocolSummary";
import { PageStatus } from "../../src/components/PageStatus";
it("localizes generated enum tokens while preserving identical authored principal strings", () => {
  const entry = productMetadata.catalog.entries.find(
    (candidate) => candidate.subject === "AUTHORITY" && candidate.kind === "REGISTER",
  )!;
  render(
    <ProtocolSummary
      entry={entry}
      value={{
        operationId: "10000000-0000-4000-8000-000000000001",
        target: {
          kind: "HUMAN",
          issuer: "https://issuer.example/",
          subject: "HUMAN",
        },
        act: {
          kind: "REGISTER",
        },
      }}
    />,
  );
  expect(
    screen.getByText("Human", {
      exact: true,
    }),
  ).toBeVisible();
  expect(
    screen.getAllByText("HUMAN", {
      exact: true,
    }),
  ).toHaveLength(2);
  expect(
    screen.getByText("Register actor", {
      exact: true,
    }),
  ).toBeVisible();
});
it("uses catalog bindings rather than field spelling and preserves whitespace as authored input", () => {
  const entry = productMetadata.catalog.entries.find(
    (candidate) => candidate.subject === "AUTHORITY" && candidate.kind === "REGISTER",
  )!;
  const { container } = render(
    <ProtocolSummary
      entry={entry}
      value={{
        kind: "HUMAN",
        target: { subject: " ", issuer: "" },
        future: { kind: "HUMAN", exact: "literal" },
      }}
    />,
  );
  expect(
    screen.queryByText("Human", {
      exact: true,
    }),
  ).toBeNull();
  expect(
    screen.getByText("HUMAN", {
      exact: true,
    }),
  ).toBeVisible();
  expect(
    screen.getAllByText("Empty input", {
      exact: true,
    }),
  ).toHaveLength(1);
  expect(container.querySelector("pre")?.textContent).toBe(
    '{\n  "kind": "HUMAN",\n  "exact": "literal"\n}',
  );
  expect([...container.querySelectorAll("bdi")].some((node) => node.textContent === " ")).toBe(
    true,
  );
});
it("an empty short nonterminal page never claims the end of visible scope", () => {
  const { rerender } = render(
    <PageStatus empty cursor="synthetic-continuation" emptyMessage="ui.noAccessibleCases" />,
  );
  expect(screen.getByText("No accessible cases returned.")).toBeVisible();
  expect(screen.getByText("More results are available in this visible scope.")).toBeVisible();
  expect(screen.queryByText("End of results in this visible scope.")).toBeNull();
  rerender(<PageStatus empty cursor={null} emptyMessage="ui.noAccessibleCases" />);
  expect(screen.getByText("End of results in this visible scope.")).toBeVisible();
});
afterEach(() => {
  vi.restoreAllMocks();
});
it("a failed history read stops loading without inventing a successful empty page", async () => {
  vi.spyOn(service, "history").mockResolvedValue({
    kind: "deliveryFailure",
    notice: {
      kind: "local",
      reason: "unreachable",
    },
    dispatched: false,
  });
  render(
    <CaseHistory
      token="synthetic"
      caseReference="SYNTHETIC"
      fields={productMetadata.catalog.fields}
    />,
  );
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(screen.queryByRole("status")).toBeNull();
  expect(screen.queryByText("No accessible history entries returned.")).toBeNull();
  expect(screen.queryByText("End of results in this visible scope.")).toBeNull();
});
